using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AccountHealthScoreFunction;

internal static class DraftRequestReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    // Accepts either the draft object directly, or { "draftJson": "<draft as a JSON string>" }.
    // The string form exists because Copilot Studio holds the draft in a string variable.
    public static async Task<(HealthScoreDraft Draft, string Error)> ReadAsync(HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body)) body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body)) return (null, "Request body is empty.");

        try
        {
            using (var doc = JsonDocument.Parse(body))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("draftJson", out var inner) &&
                    inner.ValueKind == JsonValueKind.String)
                {
                    body = inner.GetString();
                }
            }

            var draft = JsonSerializer.Deserialize<HealthScoreDraft>(body, Options);
            return draft == null ? (null, "The draft could not be read.") : (draft, null);
        }
        catch (JsonException ex)
        {
            return (null, $"The draft is not valid JSON: {ex.Message}");
        }
    }
}
