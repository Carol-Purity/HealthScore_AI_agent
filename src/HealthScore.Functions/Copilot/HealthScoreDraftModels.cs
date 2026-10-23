using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AccountHealthScoreFunction;

// The draft configuration the "Configure with AI" agent builds during the conversation.
// Mirrors the engine's structure: Profile -> Data Source -> Rule (there is no Fields layer).
//
// SAFETY:
//  - There is deliberately NO status property. CreateHealthScoreDraft always writes Draft (1).
//  - The agent NEVER writes FetchXML. Rules carry structured conditions; the server builds the
//    FetchXML deterministically (RuleFetchXmlBuilder).

public class HealthScoreDraft
{
    public DraftProfile Profile { get; set; }
    public List<DraftDataSource> DataSources { get; set; }

    // Things the admin asked for that the engine can't do (e.g. counts per account).
    // Saved into the Profile description so nothing the admin asked for is silently lost.
    public List<string> UnsupportedRequests { get; set; }
}

public class DraftProfile
{
    public string Name { get; set; }
    public string TargetTable { get; set; }
    public string Description { get; set; }
    public bool AiEnabled { get; set; } = true;
}

public class DraftDataSource
{
    public string Table { get; set; }              // logical name, e.g. "incident"
    public string RelationshipColumn { get; set; } // lookup to the target table, e.g. "customerid"
    public decimal? Weight { get; set; }           // the most this source can add to, or take from, the rule score
    public List<DraftRule> Rules { get; set; }
}

public class DraftRule
{
    public string Name { get; set; }
    public DraftFilter Filter { get; set; }
    public int Impact { get; set; }          // 1 = Positive, 2 = Negative, 3 = Neutral (evidence only)
    public decimal ScoreValue { get; set; }  // points per matching record; sign should match the Impact
}

public class DraftFilter
{
    public string Type { get; set; } = "and"; // "and" | "or"
    public List<DraftCondition> Conditions { get; set; }
    public List<DraftFilter> Filters { get; set; }
}

public class DraftCondition
{
    public string Column { get; set; }
    public string Operator { get; set; }          // FetchXML operator (eq, gt, last-x-days...) or =, !=, >, >=, <, <=
    public JsonElement? Value { get; set; }       // choice label or number, text, number, date, GUID, day count
    public List<JsonElement> Values { get; set; } // for in / not-in
}

public class DraftIssue
{
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";
}

public class RulePreview
{
    public string Path { get; set; } = "";
    public string DataSource { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string Impact { get; set; } = "";       // Positive | Negative | Neutral
    public string Condition { get; set; } = "";    // human-readable, for the review
    public decimal PointsPerMatch { get; set; }    // always positive; Impact gives the direction
    public decimal Weight { get; set; }            // the data source's cap
    public string FetchXml { get; set; } = "";
    public int? SampleMatches { get; set; }
    public decimal? SamplePoints { get; set; }     // signed, before the data source's cap
}

public class SourcePreview
{
    public string Path { get; set; } = "";
    public string DataSource { get; set; } = "";
    public decimal Weight { get; set; }
    public bool HasPositiveRule { get; set; }
    public bool HasScoringRule { get; set; }       // has a Positive or Negative rule (Neutral-only sources don't score)
    public int? PositiveMatches { get; set; }
    public decimal? Net { get; set; }              // sample: positive minus negative points
    public decimal? Contribution { get; set; }     // sample: Net limited to plus or minus Weight
}

public class DraftValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<DraftIssue> Errors { get; } = new();
    public List<DraftIssue> Warnings { get; } = new();
    public List<RulePreview> Previews { get; } = new();
    public List<SourcePreview> Sources { get; } = new();
    public string SampleRecordName { get; set; }
    public decimal? SampleScorePreview { get; set; }   // the rule score, before the AI review
    public decimal MaxRuleScore { get; set; }          // highest rule score this configuration can produce
    public bool SampleHasNoHealthySignal { get; set; } // final score would be capped at 20

    public void Error(string path, string message) => Errors.Add(new DraftIssue { Path = path, Message = message });
    public void Warn(string path, string message) => Warnings.Add(new DraftIssue { Path = path, Message = message });

    public string ToText()
    {
        if (Errors.Count == 0 && Warnings.Count == 0) return "No problems found.";
        return string.Join("\n",
            Errors.Select(e => $"Error ({e.Path}): {e.Message}")
                  .Concat(Warnings.Select(w => $"Warning ({w.Path}): {w.Message}")));
    }

    // Plain-language review, for the agent to show the admin.
    public string PreviewText()
    {
        var lines = new List<string>();

        foreach (var p in Previews)
        {
            string per = p.Impact == "Neutral"
                ? "no points (evidence for the AI review only)"
                : $"{Signed(p.Impact == "Negative" ? -p.PointsPerMatch : p.PointsPerMatch)} points per matching record";
            string line = $"• {p.RuleName} [{p.DataSource}, {p.Impact}]: {p.Condition} → {per}";
            if (p.SampleMatches.HasValue)
                line += $" (sample: {p.SampleMatches} match{(p.SampleMatches == 1 ? "" : "es")} = {Signed(p.SamplePoints ?? 0)})";
            lines.Add(line);
        }

        lines.Add("Each data source can add or take away at most its weight:");
        foreach (var s in Sources)
        {
            if (!s.HasScoringRule)
            {
                lines.Add($"• {s.DataSource}: awareness only, no points");
                continue;
            }
            string line = $"• {s.DataSource}: up to ±{s.Weight:0.##}";
            if (s.Contribution.HasValue)
                line += s.Net == s.Contribution
                    ? $" (sample: {Signed(s.Contribution.Value)})"
                    : $" (sample: {Signed(s.Net ?? 0)}, limited to {Signed(s.Contribution.Value)})";
            lines.Add(line);
        }

        lines.Add($"Scores start at 0. The highest possible rule score is {MaxRuleScore:0.##}.");

        if (SampleScorePreview.HasValue)
        {
            lines.Add($"Rule score for '{SampleRecordName}': {SampleScorePreview:0.##} / 100. The AI review then adjusts it using the record evidence.");
            if (SampleHasNoHealthySignal)
                lines.Add("No healthy signal matched on this record, so its final score can't exceed 20.");
        }

        return string.Join("\n", lines);
    }

    private static string Signed(decimal v) => v > 0 ? $"+{v:0.##}" : $"{v:0.##}";
}
