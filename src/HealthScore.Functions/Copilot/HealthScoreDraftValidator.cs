using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace AccountHealthScoreFunction;

// Checks a draft against real Dataverse metadata and against how EvaluateHealthScoreProfile works,
// builds each rule's FetchXML, and dry-runs it in Dataverse, so a rule that validates is a rule the
// engine will run. Errors = wrong, or would silently do nothing. Warnings = works, but worth knowing.
// Used by both ValidateHealthScoreDraft and CreateHealthScoreDraft, so they can never disagree.
//
// Engine structure: Profile -> Data Source -> Rule.
// Engine scoring (keep in sync with EvaluateHealthScoreProfile):
//   - The rule score starts at 0.
//   - Impact decides the direction; Score Value is the points per matching record (its sign is ignored).
//   - Each data source adds or takes away at most its weight. A source without a weight has no effect.
//   - Copilot rule: the weights of a profile's data sources may not add up to more than 100.
//   - Rule score = sum of the sources' contributions, kept between 0 and 100, rounded.
//   - The AI review then decides the final score. If no healthy (positive) signal matched,
//     the final score can't exceed 20.
internal class HealthScoreDraftValidator
{
    private const int ActiveStatus = 2;
    private const int NoEngagementCap = 20;

    private readonly ServiceClient _svc;
    private readonly DraftMetadataCache _meta;

    public HealthScoreDraftValidator(ServiceClient svc, DraftMetadataCache meta)
    {
        _svc = svc;
        _meta = meta;
    }

    // sampleRecordId (optional): a real record of the target table. Every rule is run against it
    // so the admin can see what the configuration would actually do before confirming.
    public async Task<DraftValidationResult> ValidateAsync(HealthScoreDraft draft, Guid? sampleRecordId = null)
    {
        var r = new DraftValidationResult();
        var p = draft?.Profile;
        if (p == null) { r.Error("profile", "The draft has no profile."); return r; }

        if (IsBlank(p.Name)) r.Error("profile.name", "Profile name is required.");

        EntityMetadata target = null;
        if (IsBlank(p.TargetTable))
        {
            r.Error("profile.targetTable", "Target table is required (e.g. 'account').");
        }
        else
        {
            target = await _meta.GetTableAsync(p.TargetTable);
            if (target == null) r.Error("profile.targetTable", $"Table '{p.TargetTable}' was not found in Dataverse.");
            else await CheckExistingProfilesAsync(p, r);
        }

        if (sampleRecordId.HasValue && target != null)
            await LoadSampleRecordAsync(target, sampleRecordId.Value, r);
        Guid? sample = r.SampleRecordName != null ? sampleRecordId : null;

        if (draft.DataSources == null || draft.DataSources.Count == 0)
        {
            r.Error("dataSources", "At least one data source is required.");
            return r;
        }

        var dsConfig = await _meta.GetTableAsync("reliance_healthscoredatasource");
        var ruleConfig = await _meta.GetTableAsync("reliance_healthscorerule");

        for (int i = 0; i < draft.DataSources.Count; i++)
            await ValidateDataSourceAsync(draft.DataSources[i], $"dataSources[{i}]", target, dsConfig, ruleConfig, sample, r);

        CheckWeights(r);

        if (sample.HasValue && r.IsValid)
            PreviewScore(r);

        return r;
    }

    private async Task CheckExistingProfilesAsync(DraftProfile p, DraftValidationResult r)
    {
        var q = new QueryExpression("reliance_healthscoreprofile")
        {
            ColumnSet = new ColumnSet("reliance_profilename", "reliance_status"),
            TopCount = 50
        };
        q.Criteria.AddCondition("reliance_targettable", ConditionOperator.Equal, p.TargetTable.Trim());
        var existing = (await _svc.RetrieveMultipleAsync(q)).Entities;

        if (existing.Any(e => e.GetAttributeValue<OptionSetValue>("reliance_status")?.Value == ActiveStatus))
            r.Warn("profile.targetTable",
                $"An Active profile already exists for '{p.TargetTable}'. The scoring card uses the Active profile for a table, " +
                "so the existing one would need to be deactivated before this draft is activated.");

        if (!IsBlank(p.Name) && existing.Any(e =>
                string.Equals(e.GetAttributeValue<string>("reliance_profilename")?.Trim(), p.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            r.Warn("profile.name", $"A profile named '{p.Name}' already exists for this table.");
    }

    private async Task LoadSampleRecordAsync(EntityMetadata target, Guid id, DraftValidationResult r)
    {
        try
        {
            var cols = string.IsNullOrEmpty(target.PrimaryNameAttribute) ? new ColumnSet(false) : new ColumnSet(target.PrimaryNameAttribute);
            var rec = await _svc.RetrieveAsync(target.LogicalName, id, cols);
            r.SampleRecordName = (!string.IsNullOrEmpty(target.PrimaryNameAttribute) ? rec.GetAttributeValue<string>(target.PrimaryNameAttribute) : null)
                                 ?? id.ToString();
        }
        catch (Exception)
        {
            r.Warn("sampleRecordId", $"No '{target.LogicalName}' record with id {id} was found, so no sample preview was produced.");
        }
    }

    private async Task ValidateDataSourceAsync(DraftDataSource ds, string path, EntityMetadata target,
        EntityMetadata dsConfig, EntityMetadata ruleConfig, Guid? sample, DraftValidationResult r)
    {
        if (ds == null) { r.Error(path, "Data source is empty."); return; }

        // Weight = the most this source can add to, or take from, the rule score.
        // Whether a weight is required depends on its rules, so that is checked in CheckWeights.
        if (ds.Weight is < 0)
            r.Error($"{path}.weight", "Weight can't be negative.");
        else if (ds.Weight.HasValue)
            WarnIfRounded(dsConfig, "reliance_weight", ds.Weight.Value, $"{path}.weight", r);

        if (IsBlank(ds.Table)) { r.Error($"{path}.table", "Source table is required."); return; }
        var src = await _meta.GetTableAsync(ds.Table);
        if (src == null) { r.Error($"{path}.table", $"Table '{ds.Table}' was not found in Dataverse."); return; }

        string displayName = DraftMetadataCache.Text(src.DisplayName) ?? src.LogicalName;
        var source = new SourcePreview { Path = path, DataSource = displayName, Weight = ds.Weight ?? 0 };
        r.Sources.Add(source);

        // Relationship column must be a lookup on the source table that points at the target table.
        string relationship = null;
        var rel = DraftMetadataCache.GetColumn(src, ds.RelationshipColumn);
        if (IsBlank(ds.RelationshipColumn))
            r.Error($"{path}.relationshipColumn", "Relationship column is required.");
        else if (rel == null)
            r.Error($"{path}.relationshipColumn", $"Column '{ds.RelationshipColumn}' was not found on '{src.LogicalName}'.");
        else if (target != null && !(rel is LookupAttributeMetadata lk && lk.Targets != null &&
                                     lk.Targets.Contains(target.LogicalName, StringComparer.OrdinalIgnoreCase)))
            r.Error($"{path}.relationshipColumn",
                $"'{ds.RelationshipColumn}' on '{src.LogicalName}' is not a lookup to '{target.LogicalName}', so no records would ever be found.");
        else if (target != null)
            relationship = rel.LogicalName;

        if (ds.Rules == null || ds.Rules.Count == 0)
        {
            r.Error($"{path}.rules", "At least one rule is required. A data source without rules can't affect the score.");
            return;
        }

        for (int k = 0; k < ds.Rules.Count; k++)
            await ValidateRuleAsync(ds.Rules[k], $"{path}.rules[{k}]", source, src, relationship, target, ruleConfig, sample, r);
    }

    private async Task ValidateRuleAsync(DraftRule rule, string path, SourcePreview source, EntityMetadata src,
        string relationshipColumn, EntityMetadata target, EntityMetadata ruleConfig, Guid? sample, DraftValidationResult r)
    {
        if (rule == null) { r.Error(path, "Rule is empty."); return; }
        if (IsBlank(rule.Name)) r.Error($"{path}.name", "Rule name is required.");

        // Impact decides the direction; Score Value is only the size.
        string impact = rule.Impact switch { 1 => "Positive", 2 => "Negative", 3 => "Neutral", _ => null };
        if (impact == null)
            r.Error($"{path}.impact", "Impact must be 1 (Positive), 2 (Negative) or 3 (Neutral). A rule without an Impact doesn't affect the score.");
        else if ((rule.Impact == 1 && rule.ScoreValue < 0) || (rule.Impact == 2 && rule.ScoreValue > 0))
            r.Warn($"{path}.scoreValue", $"The rule is {impact} but its Score Value is {rule.ScoreValue}. The Impact decides the direction, so the sign is ignored.");

        if (rule.Impact is 1 or 2 && rule.ScoreValue == 0)
            r.Warn($"{path}.scoreValue", "Score Value is 0, so this rule won't change the score.");
        if (rule.Impact == 3 && rule.ScoreValue != 0)
            r.Warn($"{path}.scoreValue", "Neutral rules earn no points; their matches are evidence for the AI review only.");
        WarnIfRounded(ruleConfig, "reliance_scorevalue", rule.ScoreValue, $"{path}.scoreValue", r);

        if (rule.Impact == 1) source.HasPositiveRule = true;
        if (rule.Impact is 1 or 2) source.HasScoringRule = true;

        int errorsBefore = r.Errors.Count;
        var (fetchXml, readable) = RuleFetchXmlBuilder.Build(src, relationshipColumn, rule.Filter, $"{path}.filter", r);
        bool ruleBuilt = r.Errors.Count == errorsBefore;

        if (readable == "(every related record)")
            r.Warn($"{path}.filter", "This rule has no conditions, so it matches every related record.");

        WarnIfOtherTargetLookup(rule.Filter, src, target, relationshipColumn, path, r);

        if (DraftMetadataCache.GetColumn(ruleConfig, "reliance_condition") is MemoAttributeMetadata memo &&
            memo.MaxLength is int max && fetchXml.Length > max)
        {
            r.Error($"{path}.filter", $"This rule's FetchXML is {fetchXml.Length} characters; the Condition column holds {max}. Simplify the rule or increase the column's maximum length.");
            ruleBuilt = false;
        }

        var preview = new RulePreview
        {
            Path = path,
            DataSource = source.DataSource,
            RuleName = rule.Name?.Trim() ?? "",
            Impact = impact ?? "Unset",
            Condition = readable,
            PointsPerMatch = Math.Abs(rule.ScoreValue),
            Weight = source.Weight,
            FetchXml = fetchXml
        };
        r.Previews.Add(preview);

        // Dry run, exactly as the engine runs it: the rule's FetchXML plus its own AND filter
        // that limits it to the scored record. Without a sample record, Guid.Empty returns
        // nothing but still makes Dataverse validate the query.
        if (!ruleBuilt || relationshipColumn == null) return;
        try
        {
            var doc = XDocument.Parse(fetchXml);
            doc.Root.Element("entity").Add(new XElement("filter", new XAttribute("type", "and"),
                new XElement("condition",
                    new XAttribute("attribute", relationshipColumn),
                    new XAttribute("operator", "eq"),
                    new XAttribute("value", (sample ?? Guid.Empty).ToString()))));

            var result = await _svc.RetrieveMultipleAsync(new FetchExpression(doc.ToString(SaveOptions.DisableFormatting)));
            if (sample.HasValue)
            {
                int n = result.Entities.Count;
                preview.SampleMatches = n;
                preview.SamplePoints = rule.Impact switch
                {
                    1 => n * preview.PointsPerMatch,
                    2 => -(n * preview.PointsPerMatch),
                    _ => 0m
                };
            }
        }
        catch (Exception ex)
        {
            r.Error($"{path}.filter", $"Dataverse rejected this rule's query: {ex.Message}");
        }
    }

    // The engine links a rule to the scored record through the conditions it has on lookups to the
    // target table. A second such lookup in the rule makes the link ambiguous.
    private static void WarnIfOtherTargetLookup(DraftFilter filter, EntityMetadata src, EntityMetadata target,
        string relationshipColumn, string path, DraftValidationResult r)
    {
        if (filter == null || target == null || relationshipColumn == null) return;

        var others = Columns(filter)
            .Where(c => !string.Equals(c, relationshipColumn, StringComparison.OrdinalIgnoreCase))
            .Where(c => DraftMetadataCache.GetColumn(src, c) is LookupAttributeMetadata lk && lk.Targets != null &&
                        lk.Targets.Contains(target.LogicalName, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (others.Count > 0)
            r.Warn($"{path}.filter",
                $"This rule also tests {string.Join(", ", others)}, another lookup to '{target.LogicalName}'. " +
                $"The engine will still link it to the scored record through '{relationshipColumn}'.");
    }

    private static IEnumerable<string> Columns(DraftFilter f) =>
        (f.Conditions ?? new List<DraftCondition>()).Where(c => c != null && !IsBlank(c.Column)).Select(c => c.Column.Trim())
            .Concat((f.Filters ?? new List<DraftFilter>()).Where(g => g != null).SelectMany(Columns));

    // Same warnings and maximum as the engine.
    private static void CheckWeights(DraftValidationResult r)
    {
        decimal positiveWeights = r.Sources.Where(s => s.HasPositiveRule).Sum(s => s.Weight);
        r.MaxRuleScore = positiveWeights <= 0 ? 0 : Math.Min(100, positiveWeights);

        if (r.Sources.Count > 0 && positiveWeights <= 0)
            r.Warn("dataSources", "There are no Positive rules, so the rule score can only ever be 0 and every final score is capped at 20 or less.");
        else if (positiveWeights > 0 && positiveWeights < 100)
            r.Warn("dataSources", $"The weights of data sources with Positive rules add up to {positiveWeights:0.##}, so the rule score can never exceed {positiveWeights:0.##}.");

        // A source that scores (Positive or Negative rules) needs a weight above 0.
        // A source with only Neutral rules adds evidence for the AI review, never points,
        // so its weight doesn't matter and it doesn't count towards the total.
        foreach (var s in r.Sources.Where(s => s.HasScoringRule && s.Weight <= 0))
            r.Error($"{s.Path}.weight", $"{s.DataSource} has scoring rules, so it needs a weight above 0. Without one it can't change the score.");

        // Weights share the 0-100 score range, so together they may not exceed 100.
        decimal allWeights = r.Sources.Where(s => s.HasScoringRule).Sum(s => s.Weight);
        if (allWeights > 100)
            r.Error("dataSources",
                $"The weights of scoring data sources add up to {allWeights:0.##}. They share the 0-100 score, so the total can't be more than 100. " +
                $"Reduce them by {allWeights - 100:0.##} in total.");
    }

    // Mirrors EvaluateHealthScoreProfile's rule score. Keep in sync with the engine.
    private static void PreviewScore(DraftValidationResult r)
    {
        foreach (var s in r.Sources)
        {
            var rules = r.Previews.Where(p => p.Path.StartsWith(s.Path + ".", StringComparison.Ordinal)).ToList();
            decimal net = rules.Sum(p => p.SamplePoints ?? 0);
            s.Net = net;
            s.Contribution = Math.Max(-s.Weight, Math.Min(s.Weight, net));
            s.PositiveMatches = rules.Where(p => p.Impact == "Positive").Sum(p => p.SampleMatches ?? 0);
        }

        decimal total = r.Sources.Sum(s => s.Contribution ?? 0);
        r.SampleScorePreview = Math.Round(Math.Max(0, Math.Min(100, total)), 0, MidpointRounding.AwayFromZero);

        bool hasHealthySignalSource = r.Sources.Any(s => s.HasPositiveRule);
        r.SampleHasNoHealthySignal = hasHealthySignalSource && r.Sources.Sum(s => s.PositiveMatches ?? 0) == 0;
    }

    private static void WarnIfRounded(EntityMetadata config, string column, decimal value, string path, DraftValidationResult r)
    {
        if (DraftMetadataCache.GetColumn(config, column) is IntegerAttributeMetadata && value != Math.Truncate(value))
            r.Warn(path, $"{value} will be saved as {Math.Round(value, MidpointRounding.AwayFromZero)} because this column stores whole numbers.");
    }

    private static bool IsBlank(string s) => string.IsNullOrWhiteSpace(s);
}
