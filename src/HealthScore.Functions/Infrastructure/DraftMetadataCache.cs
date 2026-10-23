using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace AccountHealthScoreFunction;

// Reads table metadata once per request and remembers it, so validating many rules on the same
// table costs one metadata call. A table that does not exist is cached as null.
public sealed class DraftMetadataCache
{
    private readonly ServiceClient _svc;
    private readonly Dictionary<string, EntityMetadata?> _tables = new(StringComparer.OrdinalIgnoreCase);

    public DraftMetadataCache(ServiceClient svc) => _svc = svc;

    public async Task<EntityMetadata?> GetTableAsync(string? logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName)) return null;
        if (_tables.TryGetValue(logicalName, out var cached)) return cached;

        EntityMetadata? metadata = null;
        try
        {
            var response = (RetrieveEntityResponse)await _svc.ExecuteAsync(new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                EntityFilters = EntityFilters.Entity | EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            });
            metadata = response.EntityMetadata;
        }
        catch (Exception)
        {
            // Unknown table: callers report it as a readiness failure.
        }

        _tables[logicalName] = metadata;
        return metadata;
    }

    public static AttributeMetadata? GetColumn(EntityMetadata? table, string? column) =>
        string.IsNullOrWhiteSpace(column)
            ? null
            : table?.Attributes?.FirstOrDefault(a => string.Equals(a.LogicalName, column.Trim(), StringComparison.OrdinalIgnoreCase));

    // The label in the user's language, or null.
    public static string? Text(Label? label) => label?.UserLocalizedLabel?.Label;

    // Option values and labels for choice, state, status and yes/no columns (same labels the engine shows).
    public static List<(int Value, string Label)> Options(AttributeMetadata? a)
    {
        if (a is BooleanAttributeMetadata b && b.OptionSet != null)
            return new List<(int, string)>
            {
                (1, Text(b.OptionSet.TrueOption?.Label) ?? "Yes"),
                (0, Text(b.OptionSet.FalseOption?.Label) ?? "No")
            };

        if (a is EnumAttributeMetadata e && e.OptionSet?.Options != null)
            return e.OptionSet.Options.Where(o => o.Value.HasValue)
                .Select(o => (o.Value!.Value, Text(o.Label) ?? o.Value!.Value.ToString()))
                .ToList();

        return new List<(int, string)>();
    }
}
