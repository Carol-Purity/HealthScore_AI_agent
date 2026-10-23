using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace AccountHealthScoreFunction;

// THE ONLY FUNCTION IN THE AI PATH THAT WRITES TO DATAVERSE.
// Called only through the agent's confirm-and-create skill, after the admin has clearly said yes.
//
// Writes the engine's structure: Profile -> Data Source -> Rule, using only the columns
// EvaluateHealthScoreProfile reads.
//
// Safety guarantees enforced here, not in the prompt:
//   1. The Profile is ALWAYS created with Status = Draft (1). The draft model has no status field at all.
//   2. The draft is re-validated server-side (including a Dataverse dry run of every rule). Any error = nothing written.
//   3. Rule FetchXML is built by RuleFetchXmlBuilder, never taken from the AI.
//   4. Everything is created in one transaction. It either all exists or none of it does.
public class CreateHealthScoreDraft
{
    private const int DraftStatus = 1;   // reliance_status: Draft=1, Active=2, Inactive=3
    private const int MaxRecords = 1000; // ExecuteTransaction limit

    // Lookup on reliance_healthscorerule that points to its Data Source (same constant as the engine)
    private const string RuleDataSourceLookup = "reliance_datasourcelookup";

    private readonly ILogger<CreateHealthScoreDraft> _logger;

    public CreateHealthScoreDraft(ILogger<CreateHealthScoreDraft> logger) => _logger = logger;

    [Function("CreateHealthScoreDraft")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        var (draft, readError) = await DraftRequestReader.ReadAsync(req);
        if (readError != null) return new BadRequestObjectResult(new { created = false, error = readError });

        using var svc = DataverseConnection.Create();
        if (!svc.IsReady)
            return new ObjectResult(new { created = false, error = "Failed to connect to Dataverse", details = svc.LastError }) { StatusCode = 500 };

        try
        {
            var cache = new DraftMetadataCache(svc);
            var validation = await new HealthScoreDraftValidator(svc, cache).ValidateAsync(draft);
            if (!validation.IsValid)
                return new BadRequestObjectResult(new
                {
                    created = false,
                    error = "The draft has validation errors, so nothing was created.",
                    errors = validation.Errors,
                    summaryText = validation.ToText()
                });

            var fetchByRulePath = validation.Previews.ToDictionary(p => p.Path, p => p.FetchXml);

            var profileConfig = await cache.GetTableAsync("reliance_healthscoreprofile");
            var dsConfig = await cache.GetTableAsync("reliance_healthscoredatasource");
            var ruleConfig = await cache.GetTableAsync("reliance_healthscorerule");

            var p = draft.Profile;
            var profileId = Guid.NewGuid();
            var records = new List<Entity>
            {
                new Entity("reliance_healthscoreprofile", profileId)
                {
                    ["reliance_profilename"] = p.Name.Trim(),
                    ["reliance_description"] = BuildDescription(p.Description, draft.UnsupportedRequests, profileConfig),
                    ["reliance_targettable"] = p.TargetTable.Trim().ToLowerInvariant(),
                    ["reliance_status"] = new OptionSetValue(DraftStatus), // ALWAYS Draft. Never Active.
                    ["reliance_aienabled"] = p.AiEnabled
                }
            };

            for (int i = 0; i < draft.DataSources.Count; i++)
            {
                var ds = draft.DataSources[i];
                var src = await cache.GetTableAsync(ds.Table);
                var dsId = Guid.NewGuid();
                string displayName = DraftMetadataCache.Text(src.DisplayName) ?? src.LogicalName;

                records.Add(new Entity("reliance_healthscoredatasource", dsId)
                {
                    // The engine reads the table from here: "Display Name (logicalname)"
                    ["reliance_datasourcename"] = $"{displayName} ({src.LogicalName})",
                    ["reliance_tablelogicalname"] = src.LogicalName, // the engine's fallback
                    ["reliance_healthscoreprofile"] = new EntityReference("reliance_healthscoreprofile", profileId),
                    ["reliance_relationshipcolumn"] = ds.RelationshipColumn.Trim().ToLowerInvariant(),
                    // Neutral-only sources don't score, so their weight is 0 when none was given
                    ["reliance_weight"] = ToColumnNumber(dsConfig, "reliance_weight", ds.Weight ?? 0m),
                    ["reliance_enabled"] = true
                });

                for (int k = 0; k < ds.Rules.Count; k++)
                {
                    var rule = ds.Rules[k];
                    records.Add(new Entity("reliance_healthscorerule")
                    {
                        ["reliance_rulename"] = rule.Name.Trim(),
                        [RuleDataSourceLookup] = new EntityReference("reliance_healthscoredatasource", dsId),
                        ["reliance_condition"] = fetchByRulePath[$"dataSources[{i}].rules[{k}]"],
                        ["reliance_impact"] = new OptionSetValue(rule.Impact),
                        // Impact decides the direction, so store the size only.
                        ["reliance_scorevalue"] = ToColumnNumber(ruleConfig, "reliance_scorevalue", Math.Abs(rule.ScoreValue)),
                        ["reliance_enabled"] = true
                    });
                }
            }

            if (records.Count > MaxRecords)
                return new BadRequestObjectResult(new { created = false, error = $"The draft would create {records.Count} records; the limit is {MaxRecords}." });

            var txn = new ExecuteTransactionRequest { Requests = new OrganizationRequestCollection(), ReturnResponses = false };
            foreach (var record in records) txn.Requests.Add(new CreateRequest { Target = record });
            await svc.ExecuteAsync(txn);

            string profileUrl = $"{DataverseConnection.Env("DataverseUrl")?.TrimEnd('/')}/main.aspx?pagetype=entityrecord&etn=reliance_healthscoreprofile&id={profileId}";

            return new OkObjectResult(new
            {
                created = true,
                profileId,
                status = "Draft",
                recordsCreated = records.Count,
                profileUrl,
                warnings = validation.Warnings,
                message = "Created as Draft. Review it, then use Review and activate on the Health Score landing page when you're ready."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateHealthScoreDraft failed.");
            return new ObjectResult(new
            {
                created = false,
                error = "Creation failed. Nothing was written, because the whole draft is created in a single transaction.",
                details = ex.Message
            }) { StatusCode = 500 };
        }
    }

    // Writes numbers in whatever type the config column really is (same root cause as fixed bug #1).
    private static object ToColumnNumber(EntityMetadata config, string column, decimal value) =>
        DraftMetadataCache.GetColumn(config, column) switch
        {
            IntegerAttributeMetadata => (int)Math.Round(value, MidpointRounding.AwayFromZero),
            DoubleAttributeMetadata => (double)value,
            MoneyAttributeMetadata => new Money(value),
            _ => value
        };

    private static string BuildDescription(string description, List<string> unsupported, EntityMetadata profileConfig)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(description)) parts.Add(description.Trim());
        parts.Add($"Drafted with the Configure with AI assistant on {DateTime.UtcNow:yyyy-MM-dd} (UTC). Reviewed and confirmed by the admin in chat before creation.");

        var items = unsupported?.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList();
        if (items?.Count > 0)
            parts.Add("Requested but not supported by the engine yet:\n- " + string.Join("\n- ", items));

        string text = string.Join("\n\n", parts);
        if (DraftMetadataCache.GetColumn(profileConfig, "reliance_description") is MemoAttributeMetadata memo &&
            memo.MaxLength is int max && text.Length > max)
            text = text.Substring(0, max);
        return text;
    }
}
