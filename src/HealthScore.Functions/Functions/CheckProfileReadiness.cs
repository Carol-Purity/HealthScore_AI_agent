using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace AccountHealthScoreFunction;

// READ-ONLY. Checks a SAVED profile (AI-drafted or manual) before activation.
// Used by the Review and activate dialog.
//
//   GET ?profileId=<guid>[&sampleRecordId=<guid>]   -> readiness checks
//
// It never changes anything. Activation itself happens in the browser as the signed-in admin.
//
// Structure (same as EvaluateHealthScoreProfile): Profile -> Data Source -> Rule. No Fields.
// - The data source's table is read from reliance_datasourcename ("opportunity" or "Opportunity (opportunity)").
// - Each rule's link to the scored record is read from its FetchXML: a condition on a lookup to the
//   profile's target table. The data source's reliance_relationshipcolumn is only a fallback.
//
// Scoring is NOT done here. The dialog's "Try it on a record" preview calls
// EvaluateHealthScoreProfile with preview=true, so there is only one scoring implementation.
public class CheckProfileReadiness
{
    private const int ActiveStatus = 2;

    // Lookup column on reliance_healthscorerule that points to its Data Source (must match the engine)
    private const string RuleDataSourceLookup = "reliance_datasourcelookup";

    private readonly ILogger<CheckProfileReadiness> _logger;

    public CheckProfileReadiness(ILogger<CheckProfileReadiness> logger) => _logger = logger;

    [Function("CheckProfileReadiness")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        if (!Guid.TryParse(req.Query["profileId"].ToString(), out var profileId))
            return new BadRequestObjectResult(new { error = "profileId is required and must be a valid GUID." });

        Guid? sampleId = null;
        string sampleParam = req.Query["sampleRecordId"].ToString().Trim();
        if (sampleParam.Length > 0)
        {
            if (!Guid.TryParse(sampleParam, out var g))
                return new BadRequestObjectResult(new { error = "sampleRecordId must be a valid GUID." });
            sampleId = g;
        }

        using var svc = DataverseConnection.Create();
        if (!svc.IsReady)
            return new ObjectResult(new { error = "Failed to connect to Dataverse", details = svc.LastError }) { StatusCode = 500 };

        Entity profile;
        try
        {
            profile = await svc.RetrieveAsync("reliance_healthscoreprofile", profileId,
                new ColumnSet("reliance_profilename", "reliance_targettable", "reliance_status"));
        }
        catch (Exception)
        {
            return new NotFoundObjectResult(new { error = $"No health score profile with id {profileId} was found." });
        }

        try
        {
            return new OkObjectResult(await CheckAsync(svc, profile, sampleId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"CheckProfileReadiness failed for profile {profileId}.");
            return new ObjectResult(new { error = "Unhandled exception", details = ex.Message }) { StatusCode = 500 };
        }
    }

    private static async Task<object> CheckAsync(ServiceClient svc, Entity profile, Guid? sampleId)
    {
        var checks = new List<ReadinessCheck>();
        void Fail(string area, string msg) => checks.Add(new ReadinessCheck { Level = "fail", Area = area, Message = msg });
        void Warn(string area, string msg) => checks.Add(new ReadinessCheck { Level = "warn", Area = area, Message = msg });
        void Pass(string area, string msg) => checks.Add(new ReadinessCheck { Level = "pass", Area = area, Message = msg });

        string profileName = profile.GetAttributeValue<string>("reliance_profilename") ?? "(unnamed profile)";
        string targetTable = ParseLogicalName(profile.GetAttributeValue<string>("reliance_targettable"));
        int status = profile.GetAttributeValue<OptionSetValue>("reliance_status")?.Value ?? 1;

        var cache = new DraftMetadataCache(svc);
        var target = await cache.GetTableAsync(targetTable);
        if (target == null)
            Fail("Profile", string.IsNullOrEmpty(targetTable) ? "No target table is set." : $"Target table '{targetTable}' was not found.");

        // Another Active profile for the same table (the dialog asks before replacing it)
        object activeProfile = null;
        if (!string.IsNullOrEmpty(targetTable))
        {
            var q = new QueryExpression("reliance_healthscoreprofile") { ColumnSet = new ColumnSet("reliance_profilename"), TopCount = 5 };
            q.Criteria.AddCondition("reliance_targettable", ConditionOperator.Equal, targetTable);
            q.Criteria.AddCondition("reliance_status", ConditionOperator.Equal, ActiveStatus);
            q.Criteria.AddCondition("reliance_healthscoreprofileid", ConditionOperator.NotEqual, profile.Id);
            var other = (await svc.RetrieveMultipleAsync(q)).Entities.FirstOrDefault();
            if (other != null)
                activeProfile = new { id = other.Id, name = other.GetAttributeValue<string>("reliance_profilename") };
        }

        // Sample record (optional). Only used to confirm each rule's query runs.
        string sampleName = null;
        if (sampleId.HasValue && target != null)
        {
            try
            {
                var cols = string.IsNullOrEmpty(target.PrimaryNameAttribute) ? new ColumnSet(false) : new ColumnSet(target.PrimaryNameAttribute);
                var rec = await svc.RetrieveAsync(target.LogicalName, sampleId.Value, cols);
                sampleName = (string.IsNullOrEmpty(target.PrimaryNameAttribute) ? null : rec.GetAttributeValue<string>(target.PrimaryNameAttribute))
                             ?? sampleId.Value.ToString();
            }
            catch (Exception)
            {
                Warn("Preview", $"No '{target.LogicalName}' record with id {sampleId} was found.");
                sampleId = null;
            }
        }
        else if (sampleId.HasValue)
        {
            sampleId = null;
        }

        // Data sources (same filters the engine uses: enabled only)
        var dsQuery = new QueryExpression("reliance_healthscoredatasource")
        {
            ColumnSet = new ColumnSet("reliance_datasourcename", "reliance_relationshipcolumn", "reliance_weight")
        };
        dsQuery.Criteria.AddCondition("reliance_healthscoreprofile", ConditionOperator.Equal, profile.Id);
        dsQuery.Criteria.AddCondition("reliance_enabled", ConditionOperator.Equal, true);
        var dataSources = (await svc.RetrieveMultipleAsync(dsQuery)).Entities;

        if (!dataSources.Any())
            Fail("Profile", "There are no enabled data sources, so nothing would be scored.");

        var dsResults = new List<ReadinessDataSource>();
        int ruleCount = 0, rulesRun = 0, brokenRules = 0;

        foreach (var ds in dataSources)
        {
            string rawName = ds.GetAttributeValue<string>("reliance_datasourcename");
            string table = ParseLogicalName(rawName);
            string fallbackRel = ParseLogicalName(ds.GetAttributeValue<string>("reliance_relationshipcolumn"));
            decimal weight = SafeDecimal(ds, "reliance_weight") ?? 0m;

            var src = await cache.GetTableAsync(table);
            string dsName = src?.DisplayName?.UserLocalizedLabel?.Label ?? rawName ?? "(unnamed data source)";

            var dsOut = new ReadinessDataSource { Name = dsName, Table = table ?? "", Weight = weight };
            dsResults.Add(dsOut);

            if (weight <= 0)
                Warn(dsName, "No weight is set, so this data source can't change the score.");

            if (string.IsNullOrEmpty(table))
            {
                Fail(dsName, "No table is selected, so the engine skips this data source.");
                continue;
            }
            if (src == null)
            {
                Fail(dsName, $"Table '{table}' was not found, so the engine skips this data source.");
                continue;
            }

            // Rules belong directly to the Data Source
            var ruleQuery = new QueryExpression("reliance_healthscorerule")
            {
                ColumnSet = new ColumnSet("reliance_rulename", "reliance_condition", "reliance_impact", "reliance_scorevalue")
            };
            ruleQuery.Criteria.AddCondition(RuleDataSourceLookup, ConditionOperator.Equal, ds.Id);
            ruleQuery.Criteria.AddCondition("reliance_enabled", ConditionOperator.Equal, true);
            var rules = (await svc.RetrieveMultipleAsync(ruleQuery)).Entities;

            if (!rules.Any())
            {
                Warn(dsName, "There are no enabled rules, so this data source can't change the score.");
                continue;
            }

            foreach (var rule in rules)
            {
                ruleCount++;
                string ruleName = rule.GetAttributeValue<string>("reliance_rulename") ?? "(unnamed rule)";

                // Same reading as the engine: Impact decides the direction, Score Value is the points
                // per matching record, and a blank Score Value counts as 1.
                decimal? rawScore = SafeDecimal(rule, "reliance_scorevalue");
                decimal score = rawScore.HasValue ? Math.Abs(rawScore.Value) : 1m;
                int impact = rule.GetAttributeValue<OptionSetValue>("reliance_impact")?.Value ?? 0;
                string area = $"{dsName}: {ruleName}";

                var rr = new ReadinessRule { RuleName = ruleName, ScoreValue = score };
                dsOut.Rules.Add(rr);

                if (impact == 0)
                    Warn(area, "No Impact is set, so this rule won't change the score.");
                else if ((impact == 1 || impact == 2) && rawScore == 0)
                    Warn(area, "Score value is 0, so this rule won't change the score.");
                else if (rawScore.HasValue && ((impact == 1 && rawScore.Value < 0) || (impact == 2 && rawScore.Value > 0)))
                    Warn(area, "The Impact and the sign of the Score Value disagree. The Impact decides the direction.");

                string fetchXml = rule.GetAttributeValue<string>("reliance_condition");
                if (string.IsNullOrWhiteSpace(fetchXml))
                {
                    Fail(area, "It has no condition, so the engine skips it.");
                    brokenRules++;
                    continue;
                }

                XDocument doc;
                try { doc = XDocument.Parse(fetchXml); }
                catch (Exception ex)
                {
                    Fail(area, $"Its condition isn't valid FetchXML: {ex.Message}");
                    brokenRules++;
                    continue;
                }

                var entity = doc.Descendants("entity").FirstOrDefault();
                if (entity == null)
                {
                    Fail(area, "Its condition has no table, so the engine skips it.");
                    brokenRules++;
                    continue;
                }

                string queried = ((string)entity.Attribute("name"))?.Trim().ToLowerInvariant();
                if (queried != table)
                {
                    Fail(area, $"It queries '{queried}', but its data source is '{table}', so the engine skips it.");
                    brokenRules++;
                    continue;
                }

                if (string.Equals((string)doc.Root?.Attribute("aggregate"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    Fail(area, "It's an aggregate query (count/sum). Rules must return individual records.");
                    brokenRules++;
                    continue;
                }

                // Which lookup links this rule's records to the scored record? (same logic as the engine)
                string rel = ResolveRelationship(src, target?.LogicalName, RootConditionColumns(entity), fallbackRel,
                                                 out string relNote);
                if (rel == null)
                {
                    Fail(area, $"It has no condition on a lookup to '{targetTable ?? "the target table"}' " +
                               "(for example the Account column set to 'Contains Data'), so the engine skips it.");
                    brokenRules++;
                    continue;
                }
                if (relNote != null)
                    Warn(area, relNote);

                // Run it the way the engine does: an extra AND filter directly under <entity>
                entity.Add(new XElement("filter", new XAttribute("type", "and"),
                    new XElement("condition",
                        new XAttribute("attribute", rel),
                        new XAttribute("operator", "eq"),
                        new XAttribute("value", (sampleId ?? Guid.Empty).ToString()))));

                try
                {
                    var result = await svc.RetrieveMultipleAsync(new FetchExpression(doc.ToString(SaveOptions.DisableFormatting)));
                    rr.Runs = true;
                    rulesRun++;
                    if (sampleId.HasValue)
                        rr.SampleMatches = result.Entities.Count;
                }
                catch (Exception ex)
                {
                    Fail(area, $"Dataverse rejected its query: {ex.Message}");
                    brokenRules++;
                }
            }
        }

        decimal totalWeight = dsResults.Sum(d => d.Weight);
        if (totalWeight > 100)
            Warn("Profile", $"Data source weights add up to {totalWeight:0.##}, which is more than 100.");

        if (dataSources.Any() && ruleCount == 0)
            Fail("Profile", "There are no enabled rules, so nothing would be scored.");
        else if (ruleCount > 0)
            Pass("Profile", $"{dataSources.Count} data source{(dataSources.Count == 1 ? "" : "s")}, {ruleCount} rule{(ruleCount == 1 ? "" : "s")}");

        if (ruleCount > 0 && rulesRun == ruleCount)
            Pass("Rules", "Every rule's query runs in Dataverse");
        if (ruleCount > 0 && brokenRules == 0)
            Pass("Rules", "Every rule is limited to the record being scored");

        return new
        {
            profileId = profile.Id,
            profileName,
            targetTable,
            status = status == ActiveStatus ? "Active" : status == 3 ? "Inactive" : "Draft",
            ready = checks.All(c => c.Level != "fail"),
            failCount = checks.Count(c => c.Level == "fail"),
            warnCount = checks.Count(c => c.Level == "warn"),
            checks = checks.OrderBy(c => c.Level == "fail" ? 0 : c.Level == "warn" ? 1 : 2).ToList(),
            activeProfile,
            sampleRecordName = sampleName,
            // The preview score comes from EvaluateHealthScoreProfile (preview=true), not from here.
            sampleScorePreview = (decimal?)null,
            dataSources = dsResults
        };
    }

    // The rule's condition on a lookup to the target table; falls back to the data source's relationship column.
    private static string ResolveRelationship(EntityMetadata src, string targetTable, List<string> conditionColumns,
                                              string fallbackRel, out string note)
    {
        note = null;

        bool PointsToTarget(string column) =>
            !string.IsNullOrEmpty(targetTable) &&
            DraftMetadataCache.GetColumn(src, column) is LookupAttributeMetadata lk &&
            lk.Targets != null && lk.Targets.Contains(targetTable, StringComparer.OrdinalIgnoreCase);

        var candidates = conditionColumns.Where(PointsToTarget).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (candidates.Count == 1)
            return candidates[0];

        if (candidates.Count > 1)
        {
            var chosen = candidates.FirstOrDefault(c => c.Equals(fallbackRel, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
            note = $"It has conditions on several '{targetTable}' lookups ({string.Join(", ", candidates)}); the engine will use '{chosen}'.";
            return chosen;
        }

        if (!string.IsNullOrEmpty(fallbackRel) && PointsToTarget(fallbackRel))
        {
            note = $"It has no condition on a '{targetTable}' lookup, so the engine will use the data source's relationship column '{fallbackRel}'. " +
                   "Adding the condition (e.g. Account 'Contains Data') makes the rule self-contained.";
            return fallbackRel;
        }

        return null;
    }

    // Condition columns on the rule's main table (not on related tables)
    private static List<string> RootConditionColumns(XElement entity) =>
        entity.Descendants("condition")
            .Where(c => c.Attribute("entityname") == null &&
                        c.Ancestors().TakeWhile(a => a != entity).All(a => a.Name.LocalName != "link-entity"))
            .Select(c => (string)c.Attribute("attribute"))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

    // Accepts "opportunity" or "Opportunity (opportunity)" and returns "opportunity"
    private static string ParseLogicalName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"\(([^()]+)\)\s*$");
        return (match.Success ? match.Groups[1].Value : value).Trim().ToLowerInvariant();
    }

    private static decimal? SafeDecimal(Entity entity, string fieldName)
    {
        if (!entity.Contains(fieldName)) return null;
        return entity[fieldName] switch
        {
            int i => i,
            decimal d => d,
            double dbl => (decimal)dbl,
            Money m => m.Value,
            _ => null
        };
    }
}

public class ReadinessCheck
{
    public string Level { get; set; } = "";   // pass | warn | fail
    public string Area { get; set; } = "";
    public string Message { get; set; } = "";
}

public class ReadinessDataSource
{
    public string Name { get; set; } = "";
    public string Table { get; set; } = "";
    public decimal Weight { get; set; }
    public List<ReadinessRule> Rules { get; set; } = new();
}

public class ReadinessRule
{
    public string RuleName { get; set; } = "";
    public decimal ScoreValue { get; set; }
    public bool Runs { get; set; }
    public int? SampleMatches { get; set; }
    public decimal? SampleWeightedPoints { get; set; }
}
