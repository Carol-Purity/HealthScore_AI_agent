using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Metadata;

namespace AccountHealthScoreFunction;

// Turns a rule's structured conditions into FetchXML. The AI never writes XML itself.
//
// Output shape (always):
//   <fetch><entity name="{table}"><attribute name="{primary id}"/>
//     <filter type="and">
//       <condition attribute="{relationship column}" operator="not-null"/>
//       ...conditions... [<filter type="or">...</filter>]
//     </filter>
//   </entity></fetch>
//
// The "not-null" (Contains Data) condition on the relationship column is how
// EvaluateHealthScoreProfile finds which lookup links each rule to the record being scored.
// The engine then adds its own AND filter with that lookup equal to the scored record.
internal static class RuleFetchXmlBuilder
{
    private const int MaxDepth = 3;

    public static (string FetchXml, string Readable) Build(EntityMetadata table, string relationshipColumn, DraftFilter filter, string path, DraftValidationResult r)
    {
        var top = new XElement("filter", new XAttribute("type", "and"));
        string readable = "";

        // Link to the scored record. Not part of the readable text: every rule has it.
        if (!string.IsNullOrWhiteSpace(relationshipColumn))
            top.Add(new XElement("condition",
                new XAttribute("attribute", relationshipColumn.Trim().ToLowerInvariant()),
                new XAttribute("operator", "not-null")));

        if (filter != null && HasContent(filter))
        {
            if (string.Equals(filter.Type?.Trim(), "or", StringComparison.OrdinalIgnoreCase))
            {
                var (el, text) = BuildFilter(table, filter, path, 1, r);
                top.Add(el);
                readable = text;
            }
            else
            {
                NormalizeType(filter.Type, path, r);
                readable = AddContents(top, table, filter, "and", path, 1, r);
            }
        }

        var fetch = new XElement("fetch",
            new XElement("entity", new XAttribute("name", table.LogicalName),
                new XElement("attribute", new XAttribute("name", table.PrimaryIdAttribute)),
                top));

        return (fetch.ToString(SaveOptions.DisableFormatting),
                string.IsNullOrEmpty(readable) ? "(every related record)" : readable);
    }

    private static (XElement, string) BuildFilter(EntityMetadata table, DraftFilter f, string path, int depth, DraftValidationResult r)
    {
        string type = NormalizeType(f.Type, path, r);
        var el = new XElement("filter", new XAttribute("type", type));
        return (el, AddContents(el, table, f, type, path, depth, r));
    }

    private static string AddContents(XElement target, EntityMetadata table, DraftFilter f, string type, string path, int depth, DraftValidationResult r)
    {
        if (depth > MaxDepth)
        {
            r.Error(path, $"Condition groups can be nested at most {MaxDepth} levels deep.");
            return "";
        }

        var parts = new List<string>();

        var conditions = f.Conditions ?? new List<DraftCondition>();
        for (int i = 0; i < conditions.Count; i++)
        {
            var (el, text) = BuildCondition(table, conditions[i], $"{path}.conditions[{i}]", r);
            if (el == null) continue;
            target.Add(el);
            parts.Add(text);
        }

        var groups = f.Filters ?? new List<DraftFilter>();
        for (int i = 0; i < groups.Count; i++)
        {
            string gPath = $"{path}.filters[{i}]";
            if (groups[i] == null || !HasContent(groups[i])) { r.Error(gPath, "Empty condition group."); continue; }
            var (el, text) = BuildFilter(table, groups[i], gPath, depth + 1, r);
            target.Add(el);
            parts.Add($"({text})");
        }

        return string.Join(type == "or" ? " OR " : " AND ", parts);
    }

    private static (XElement, string) BuildCondition(EntityMetadata table, DraftCondition c, string path, DraftValidationResult r)
    {
        if (c == null || string.IsNullOrWhiteSpace(c.Column)) { r.Error(path, "Condition needs a column."); return (null, null); }

        var col = DraftMetadataCache.GetColumn(table, c.Column);
        if (col == null) { r.Error(path, $"Column '{c.Column}' was not found on '{table.LogicalName}'."); return (null, null); }

        var kind = EngineCapabilities.Kind(col);
        if (kind == ColumnKind.Unsupported)
        {
            r.Error(path, $"'{col.LogicalName}' ({col.AttributeType}) can't be used in a condition.");
            return (null, null);
        }

        string op = EngineCapabilities.NormalizeOperator(c.Operator);
        var allowed = EngineCapabilities.OperatorsFor(kind);
        if (op == null || !allowed.Contains(op))
        {
            r.Error(path, $"Operator '{c.Operator}' isn't available for {kind.ToString().ToLowerInvariant()} column '{col.LogicalName}'. Use one of: {string.Join(", ", allowed)}.");
            return (null, null);
        }

        string name = DraftMetadataCache.Text(col.DisplayName) ?? col.LogicalName;
        var el = new XElement("condition", new XAttribute("attribute", col.LogicalName), new XAttribute("operator", op));

        if (EngineCapabilities.ValuelessOperators.Contains(op))
            return (el, Readable(name, op, null));

        var raws = new List<string>();
        if (c.Values != null) raws.AddRange(c.Values.Select(Raw));
        if (raws.Count == 0 && c.Value.HasValue) raws.Add(Raw(c.Value.Value));
        raws = raws.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

        if (EngineCapabilities.MultiValueOperators.Contains(op))
        {
            if (raws.Count == 0) { r.Error(path, $"'{op}' needs at least one value."); return (null, null); }
            var shown = new List<string>();
            foreach (var raw in raws)
            {
                var (v, display) = Convert(col, kind, op, raw, path, r);
                if (v == null) return (null, null);
                el.Add(new XElement("value", v));
                shown.Add(display);
            }
            return (el, Readable(name, op, string.Join(", ", shown)));
        }

        if (raws.Count != 1) { r.Error(path, $"'{op}' needs exactly one value."); return (null, null); }

        var (value, shownValue) = Convert(col, kind, op, raws[0], path, r);
        if (value == null) return (null, null);
        el.Add(new XAttribute("value", value));
        return (el, Readable(name, op, shownValue));
    }

    // Returns (value for FetchXML, value to show the admin), or (null, null) after recording an error.
    private static (string, string) Convert(AttributeMetadata col, ColumnKind kind, string op, string raw, string path, DraftValidationResult r)
    {
        raw = raw.Trim();

        if (EngineCapabilities.CountOperators.Contains(op))
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                return (n.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture));
            r.Error(path, $"'{op}' needs a whole number greater than 0, not '{raw}'.");
            return (null, null);
        }

        switch (kind)
        {
            case ColumnKind.Choice:
            case ColumnKind.Boolean:
            {
                var options = DraftMetadataCache.Options(col);
                if (kind == ColumnKind.Boolean)
                {
                    if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)) raw = "1";
                    if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw.Equals("no", StringComparison.OrdinalIgnoreCase)) raw = "0";
                }

                var match = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num)
                    ? options.FirstOrDefault(o => o.Value == num)
                    : options.FirstOrDefault(o => o.Label.Equals(raw, StringComparison.OrdinalIgnoreCase));

                if (match.Label != null)
                    return (match.Value.ToString(CultureInfo.InvariantCulture), match.Label);

                r.Error(path, $"'{raw}' isn't an option of '{col.LogicalName}'. Options: {string.Join(", ", options.Select(o => $"{o.Label} ({o.Value})"))}.");
                return (null, null);
            }

            case ColumnKind.Number:
                if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d))
                {
                    if (col.AttributeType is AttributeTypeCode.Integer or AttributeTypeCode.BigInt && d != Math.Truncate(d))
                    {
                        r.Error(path, $"'{col.LogicalName}' holds whole numbers, so '{raw}' can't be used.");
                        return (null, null);
                    }
                    string s = d.ToString(CultureInfo.InvariantCulture);
                    return (s, s);
                }
                r.Error(path, $"'{raw}' is not a number.");
                return (null, null);

            case ColumnKind.Date:
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    string s = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return (s, s);
                }
                r.Error(path, $"'{raw}' is not a date. Use the format yyyy-MM-dd.");
                return (null, null);

            case ColumnKind.Lookup:
                if (Guid.TryParse(raw, out var g)) return (g.ToString(), g.ToString());
                r.Error(path, $"'{col.LogicalName}' is a lookup, so its value must be a record GUID, not '{raw}'.");
                return (null, null);

            default: // Text
                if ((op == "like" || op == "not-like") && !raw.Contains('%'))
                    r.Warn(path, $"'{raw}' has no % wildcard, so '{op}' behaves like an exact match.");
                return (raw, raw);
        }
    }

    private static string Readable(string name, string op, string v) => op switch
    {
        "eq" => $"{name} = {v}",
        "ne" => $"{name} ≠ {v}",
        "gt" => $"{name} > {v}",
        "ge" => $"{name} ≥ {v}",
        "lt" => $"{name} < {v}",
        "le" => $"{name} ≤ {v}",
        "like" => $"{name} is like '{v}'",
        "not-like" => $"{name} is not like '{v}'",
        "begins-with" => $"{name} begins with '{v}'",
        "ends-with" => $"{name} ends with '{v}'",
        "in" => $"{name} is one of ({v})",
        "not-in" => $"{name} is not one of ({v})",
        "null" => $"{name} is empty",
        "not-null" => $"{name} has a value",
        "on" => $"{name} is on {v}",
        "on-or-before" => $"{name} is on or before {v}",
        "on-or-after" => $"{name} is on or after {v}",
        "last-x-days" => $"{name} is in the last {v} days",
        "next-x-days" => $"{name} is in the next {v} days",
        "olderthan-x-days" => $"{name} is older than {v} days",
        "last-x-months" => $"{name} is in the last {v} months",
        "next-x-months" => $"{name} is in the next {v} months",
        "olderthan-x-months" => $"{name} is older than {v} months",
        _ => $"{name} is {op.Replace('-', ' ')}" // today, this-month, ...
    };

    private static string NormalizeType(string type, string path, DraftValidationResult r)
    {
        if (string.IsNullOrWhiteSpace(type)) return "and";
        var t = type.Trim().ToLowerInvariant();
        if (t is "and" or "or") return t;
        r.Error(path, $"Group type must be 'and' or 'or', not '{type}'.");
        return "and";
    }

    private static bool HasContent(DraftFilter f) => (f.Conditions?.Count ?? 0) + (f.Filters?.Count ?? 0) > 0;

    private static string Raw(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText()
    };
}
