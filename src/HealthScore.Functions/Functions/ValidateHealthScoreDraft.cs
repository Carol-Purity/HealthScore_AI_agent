using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AccountHealthScoreFunction;

// READ-ONLY. The agent calls this before every review summary.
//   POST body: the draft (or { "draftJson": "..." })
//   ?sampleRecordId=<guid of a target-table record>  (optional) -> per-rule matches + the rule score
//   (the score before the engine's AI review, which can still adjust it)
// Always returns 200 when the draft could be read. Validation problems are data, not HTTP errors.
public class ValidateHealthScoreDraft
{
    private readonly ILogger<ValidateHealthScoreDraft> _logger;

    public ValidateHealthScoreDraft(ILogger<ValidateHealthScoreDraft> logger) => _logger = logger;

    [Function("ValidateHealthScoreDraft")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        Guid? sampleRecordId = null;
        string sampleParam = req.Query["sampleRecordId"].ToString().Trim();
        if (!string.IsNullOrEmpty(sampleParam))
        {
            if (!Guid.TryParse(sampleParam, out var g))
                return new BadRequestObjectResult(new { error = "sampleRecordId must be a valid GUID." });
            sampleRecordId = g;
        }

        var (draft, readError) = await DraftRequestReader.ReadAsync(req);
        if (readError != null) return new BadRequestObjectResult(new { error = readError });

        using var svc = DataverseConnection.Create();
        if (!svc.IsReady)
            return new ObjectResult(new { error = "Failed to connect to Dataverse", details = svc.LastError }) { StatusCode = 500 };

        try
        {
            var result = await new HealthScoreDraftValidator(svc, new DraftMetadataCache(svc)).ValidateAsync(draft, sampleRecordId);

            return new OkObjectResult(new
            {
                isValid = result.IsValid,
                errorCount = result.Errors.Count,
                warningCount = result.Warnings.Count,
                errors = result.Errors,
                warnings = result.Warnings,
                sampleRecordName = result.SampleRecordName,
                sampleScorePreview = result.SampleScorePreview,
                sampleHasNoHealthySignal = result.SampleHasNoHealthySignal,
                maxRuleScore = result.MaxRuleScore,
                rules = result.Previews,
                dataSources = result.Sources,
                summaryText = result.ToText(),
                previewText = result.PreviewText()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidateHealthScoreDraft failed.");
            return new ObjectResult(new { error = "Unhandled exception", details = ex.Message }) { StatusCode = 500 };
        }
    }
}
