using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;

namespace AccountHealthScoreFunction;

// READ-ONLY. Lets the Copilot agent use real table/column names and real option labels
// instead of guessing them.
//
//   ?targetTable=account                                   -> tables that link to account, with relationship columns
//   ?targetTable=account&search=case                       -> same, filtered
//   ?targetTable=account&sourceTable=incident              -> relationship columns + usable columns + operators
//   ?targetTable=account&sourceTable=incident&search=prio  -> same, columns filtered
public class GetScoringMetadata
{
    private const int MaxCandidates = 60;
    private readonly ILogger<GetScoringMetadata> _logger;

    public GetScoringMetadata(ILogger<GetScoringMetadata> logger) => _logger = logger;

    [Function("GetScoringMetadata")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        string targetTable = req.Query["targetTable"].ToString().Trim().ToLowerInvariant();
        string sourceTable = req.Query["sourceTable"].ToString().Trim().ToLowerInvariant();
        string search = req.Query["search"].ToString().Trim();

        if (string.IsNullOrEmpty(targetTable))
            return new BadRequestObjectResult(new { error = "targetTable is required (e.g. 'account')." });

        using var svc = DataverseConnection.Create();
        if (!svc.IsReady)
            return new ObjectResult(new { error = "Failed to connect to Dataverse", details = svc.LastError }) { StatusCode = 500 };

        try
        {
            return string.IsNullOrEmpty(sourceTable)
                ? await DescribeCandidateSourcesAsync(svc, targetTable, search)
                : await DescribeSourceAsync(svc, targetTable, sourceTable, search);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"GetScoringMetadata failed for target '{targetTable}', source '{sourceTable}'.");
            return new ObjectResult(new { error = "Unhandled exception", details = ex.Message }) { StatusCode = 500 };
        }
    }

    private static async Task<IActionResult> DescribeCandidateSourcesAsync(ServiceClient svc, string targetTable, string search)
    {
        EntityMetadata target;
        try
        {
            var resp = (RetrieveEntityResponse)await svc.ExecuteAsync(new RetrieveEntityRequest
            {
                LogicalName = targetTable,
                EntityFilters = EntityFilters.Entity | EntityFilters.Relationships,
                RetrieveAsIfPublished = true
            });
            target = resp.EntityMetadata;
        }
        catch (Exception)
        {
            return new NotFoundObjectResult(new { error = $"Table '{targetTable}' was not found." });
        }

        // Every 1:N relationship from the target = a table with a lookup back to it.
        var byTable = (target.OneToManyRelationships ?? Array.Empty<OneToManyRelationshipMetadata>())
            .Where(rel => !string.IsNullOrEmpty(rel.ReferencingEntity) && !string.IsNullOrEmpty(rel.ReferencingAttribute))
            .GroupBy(rel => rel.ReferencingEntity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(rel => rel.ReferencingAttribute).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
                StringComparer.OrdinalIgnoreCase);

        if (byTable.Count == 0)
            return new OkObjectResult(new { targetTable, candidateCount = 0, candidates = Array.Empty<object>() });

        // One metadata call for display names; drops intersect and hidden system tables.
        var filter = new MetadataFilterExpression(LogicalOperator.And);
        filter.Conditions.Add(new MetadataConditionExpression("LogicalName", MetadataConditionOperator.In, byTable.Keys.ToArray()));
        var meta = (RetrieveMetadataChangesResponse)await svc.ExecuteAsync(new RetrieveMetadataChangesRequest
        {
            Query = new EntityQueryExpression
            {
                Criteria = filter,
                Properties = new MetadataPropertiesExpression("LogicalName", "DisplayName", "IsIntersect", "IsValidForAdvancedFind")
            }
        });

        var candidates = meta.EntityMetadata
            .Where(e => e.IsIntersect != true && e.IsValidForAdvancedFind == true)
            .Select(e => new
            {
                table = e.LogicalName,
                displayName = DraftMetadataCache.Text(e.DisplayName) ?? e.LogicalName,
                relationshipColumns = byTable[e.LogicalName]
            })
            .Where(c => string.IsNullOrEmpty(search) ||
                        c.table.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        c.displayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.displayName)
            .ToList();

        return new OkObjectResult(new
        {
            targetTable,
            targetDisplayName = DraftMetadataCache.Text(target.DisplayName) ?? targetTable,
            candidateCount = candidates.Count,
            truncated = candidates.Count > MaxCandidates,
            hint = candidates.Count > MaxCandidates ? "Call again with 'search' to narrow the list." : null,
            candidates = candidates.Take(MaxCandidates)
        });
    }

    private static async Task<IActionResult> DescribeSourceAsync(ServiceClient svc, string targetTable, string sourceTable, string search)
    {
        var src = await new DraftMetadataCache(svc).GetTableAsync(sourceTable);
        if (src == null) return new NotFoundObjectResult(new { error = $"Table '{sourceTable}' was not found." });

        var attrs = src.Attributes ?? Array.Empty<AttributeMetadata>();

        var relationshipColumns = attrs.OfType<LookupAttributeMetadata>()
            .Where(a => a.Targets != null && a.Targets.Contains(targetTable, StringComparer.OrdinalIgnoreCase))
            .Select(a => new
            {
                column = a.LogicalName,
                displayName = DraftMetadataCache.Text(a.DisplayName) ?? a.LogicalName,
                pointsTo = a.Targets
            })
            .ToList();

        var columns = attrs
            .Where(a => EngineCapabilities.Kind(a) != ColumnKind.Unsupported && a.IsValidForAdvancedFind?.Value != false)
            .Select(a => new
            {
                column = a.LogicalName,
                displayName = DraftMetadataCache.Text(a.DisplayName) ?? a.LogicalName,
                type = EngineCapabilities.KindName(a),
                options = EngineCapabilities.Kind(a) is ColumnKind.Choice or ColumnKind.Boolean
                    ? DraftMetadataCache.Options(a).Select(o => new { value = o.Value, label = o.Label }).ToList()
                    : null
            })
            .Where(c => string.IsNullOrEmpty(search) ||
                        c.column.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        c.displayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.displayName)
            .ToList();

        return new OkObjectResult(new
        {
            targetTable,
            sourceTable = src.LogicalName,
            sourceDisplayName = DraftMetadataCache.Text(src.DisplayName) ?? src.LogicalName,
            relationshipColumns,
            columns,
            operatorsByType = EngineCapabilities.OperatorCatalogue(),
            notes = relationshipColumns.Count == 0
                ? $"'{src.LogicalName}' has no lookup to '{targetTable}', so it can't be a data source for this profile."
                : null
        });
    }
}
