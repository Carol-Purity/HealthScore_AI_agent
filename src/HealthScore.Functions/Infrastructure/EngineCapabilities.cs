using Microsoft.Xrm.Sdk.Metadata;

namespace AccountHealthScoreFunction;

// The column types and FetchXML operators the Copilot path may use. Deliberately curated:
// every operator here has a plain-language meaning an admin can review.
internal enum ColumnKind { Unsupported, Text, Number, Date, Choice, Boolean, Lookup }

internal static class EngineCapabilities
{
    public static readonly HashSet<string> ValuelessOperators = new(StringComparer.OrdinalIgnoreCase)
        { "null", "not-null", "today", "yesterday", "this-week", "last-week", "this-month", "last-month", "this-year", "last-year" };

    public static readonly HashSet<string> MultiValueOperators = new(StringComparer.OrdinalIgnoreCase) { "in", "not-in" };

    public static readonly HashSet<string> CountOperators = new(StringComparer.OrdinalIgnoreCase)
        { "last-x-days", "next-x-days", "olderthan-x-days", "last-x-months", "next-x-months", "olderthan-x-months" };

    private static readonly string[] Common = { "eq", "ne", "null", "not-null" };

    private static readonly Dictionary<ColumnKind, string[]> Operators = new()
    {
        [ColumnKind.Text] = Common.Concat(new[] { "in", "not-in", "like", "not-like", "begins-with", "ends-with" }).ToArray(),
        [ColumnKind.Number] = Common.Concat(new[] { "gt", "ge", "lt", "le", "in", "not-in" }).ToArray(),
        [ColumnKind.Date] = new[]
        {
            "null", "not-null", "on", "on-or-before", "on-or-after",
            "last-x-days", "next-x-days", "olderthan-x-days", "last-x-months", "next-x-months", "olderthan-x-months",
            "today", "yesterday", "this-week", "last-week", "this-month", "last-month", "this-year", "last-year"
        },
        [ColumnKind.Choice] = Common.Concat(new[] { "in", "not-in" }).ToArray(),
        [ColumnKind.Boolean] = Common,
        [ColumnKind.Lookup] = Common.Concat(new[] { "in", "not-in" }).ToArray(),
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = "eq", ["=="] = "eq", ["!="] = "ne", ["<>"] = "ne",
        [">"] = "gt", [">="] = "ge", ["<"] = "lt", ["<="] = "le"
    };

    public static string[] OperatorsFor(ColumnKind kind) => Operators.TryGetValue(kind, out var ops) ? ops : Array.Empty<string>();

    public static object OperatorCatalogue() => Operators.ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv => kv.Value);

    public static string NormalizeOperator(string op)
    {
        if (string.IsNullOrWhiteSpace(op)) return null;
        op = op.Trim();
        return Aliases.TryGetValue(op, out var mapped) ? mapped : op.ToLowerInvariant();
    }

    public static ColumnKind Kind(AttributeMetadata a)
    {
        if (a == null || !string.IsNullOrEmpty(a.AttributeOf) || a.IsValidForRead == false) return ColumnKind.Unsupported;
        return a.AttributeType switch
        {
            AttributeTypeCode.String or AttributeTypeCode.Memo => ColumnKind.Text,
            AttributeTypeCode.Integer or AttributeTypeCode.BigInt or AttributeTypeCode.Decimal
                or AttributeTypeCode.Double or AttributeTypeCode.Money => ColumnKind.Number,
            AttributeTypeCode.DateTime => ColumnKind.Date,
            AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status => ColumnKind.Choice,
            AttributeTypeCode.Boolean => ColumnKind.Boolean,
            AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner => ColumnKind.Lookup,
            _ => ColumnKind.Unsupported // multi-select choice, party list, file, image...
        };
    }

    public static string KindName(AttributeMetadata a) => Kind(a).ToString().ToLowerInvariant();
}
