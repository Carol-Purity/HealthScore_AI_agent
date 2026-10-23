using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace AccountHealthScoreFunction;

// READ-ONLY. Returns a profile's full configuration as JSON (for front ends and the copilot):
// the profile, its enabled data sources, and each data source's enabled rules.
//
//   GET ?profileId=<guid>
//
// Structure: Profile -> Data Source -> Rule (rules link to their data source through reliance_datasourcelookup).
public class GetHealthScoreConfig
{
    private const string RuleDataSourceLookup = "reliance_datasourcelookup";
    private readonly ILogger<GetHealthScoreConfig> _logger;

    public GetHealthScoreConfig(ILogger<GetHealthScoreConfig> logger) => _logger = logger;

    [Function("GetHealthScoreConfig")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        string? profileId = req.Query["profileId"];
        if (string.IsNullOrEmpty(profileId) || !Guid.TryParse(profileId, out Guid profileGuid))
            return new BadRequestObjectResult(new { error = "profileId is required and must be a valid GUID" });

        using var svc = DataverseConnection.Create();
        if (!svc.IsReady)
            return new ObjectResult(new { error = "Failed to connect to Dataverse", details = svc.LastError }) { StatusCode = 500 };

        Entity profile;
        try
        {
            profile = await svc.RetrieveAsync("reliance_healthscoreprofile", profileGuid,
                new ColumnSet("reliance_profilename", "reliance_description", "reliance_targettable", "reliance_status", "reliance_aienabled"));
        }
        catch (Exception)
        {
            return new NotFoundObjectResult(new { error = $"No health score profile with id {profileGuid} was found." });
        }

        try
        {
            var dsQuery = new QueryExpression("reliance_healthscoredatasource")
            {
                ColumnSet = new ColumnSet("reliance_datasourcename", "reliance_relationshipcolumn", "reliance_weight")
            };
            dsQuery.Criteria.AddCondition("reliance_healthscoreprofile", ConditionOperator.Equal, profileGuid);
            dsQuery.Criteria.AddCondition("reliance_enabled", ConditionOperator.Equal, true);
            var dataSources = (await svc.RetrieveMultipleAsync(dsQuery)).Entities;

            var dataSourceResults = new List<object>();
            foreach (var ds in dataSources)
            {
                var ruleQuery = new QueryExpression("reliance_healthscorerule")
                {
                    ColumnSet = new ColumnSet("reliance_rulename", "reliance_condition", "reliance_impact", "reliance_scorevalue")
                };
                ruleQuery.Criteria.AddCondition(RuleDataSourceLookup, ConditionOperator.Equal, ds.Id);
                ruleQuery.Criteria.AddCondition("reliance_enabled", ConditionOperator.Equal, true);
                var rules = (await svc.RetrieveMultipleAsync(ruleQuery)).Entities;

                dataSourceResults.Add(new
                {
                    dataSourceId = ds.Id,
                    table = ds.GetAttributeValue<string>("reliance_datasourcename"),
                    relationshipColumn = ds.GetAttributeValue<string>("reliance_relationshipcolumn"),
                    weight = SafeDecimal(ds, "reliance_weight"),
                    rules = rules.Select(r => new
                    {
                        ruleId = r.Id,
                        ruleName = r.GetAttributeValue<string>("reliance_rulename"),
                        condition = r.GetAttributeValue<string>("reliance_condition"),
                        impact = ImpactLabel(r.GetAttributeValue<OptionSetValue>("reliance_impact")?.Value),
                        scoreValue = SafeDecimal(r, "reliance_scorevalue")
                    }).ToList()
                });
            }

            return new OkObjectResult(new
            {
                profileId = profileGuid,
                profileName = profile.GetAttributeValue<string>("reliance_profilename"),
                description = profile.GetAttributeValue<string>("reliance_description"),
                targetTable = profile.GetAttributeValue<string>("reliance_targettable"),
                status = StatusLabel(profile.GetAttributeValue<OptionSetValue>("reliance_status")?.Value),
                aiEnabled = profile.GetAttributeValue<bool?>("reliance_aienabled"),
                dataSources = dataSourceResults
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Reading config failed for profile {profileId}.");
            return new ObjectResult(new { error = "Unhandled exception", message = ex.Message }) { StatusCode = 500 };
        }
    }

    private static string StatusLabel(int? value) => value switch { 1 => "Draft", 2 => "Active", 3 => "Inactive", _ => "Unknown" };

    private static string ImpactLabel(int? value) => value switch { 1 => "Positive", 2 => "Negative", 3 => "Neutral", _ => "Unset" };

    private static decimal? SafeDecimal(Entity entity, string field) => entity.Contains(field)
        ? entity[field] switch { int i => i, decimal d => d, double dbl => (decimal)dbl, Money m => m.Value, _ => null }
        : null;
}
