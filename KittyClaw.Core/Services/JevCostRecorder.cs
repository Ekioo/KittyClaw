using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>Accounts for each billed API response independently of its resulting judgment.</summary>
public sealed class JevCostRecorder(ProjectService projects, CostTracker costs,
    CostReportService reports, ILogger<JevCostRecorder> logger)
{
    public async Task<string?> RecordAsync(string slug, Ticket ticket, string purpose, JsonElement response, double durationSeconds)
    {
        try
        {
            var model = response.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
                ? modelValue.GetString() ?? "jev-unknown" : "jev-unknown";
            var input = 0; var output = 0;
            var hasUsage = response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("input_tokens", out var inputValue) && inputValue.ValueKind == JsonValueKind.Number && inputValue.TryGetInt32(out input) && input >= 0
                && usage.TryGetProperty("output_tokens", out var outputValue) && outputValue.ValueKind == JsonValueKind.Number && outputValue.TryGetInt32(out output) && output >= 0;
            if (!hasUsage) { input = 0; output = 0; }
            decimal usd = 0;
            var priced = hasUsage && ModelCostEstimator.TryEstimate(model, input, output, 0, 0, out usd);
            var project = await projects.GetProjectAsync(slug);
            if (project is null) return null;
            var recordId = "jev-" + Guid.NewGuid().ToString("N");
            costs.LogRun(projects.ResolveWorkspacePath(project), new CostLogEntry(
                DateTime.UtcNow, "JEV " + purpose, ticket.Id, model, input, output, 0, 0, usd,
                durationSeconds, 0, CostEstimated: priced, ProjectSlug: slug, PipelineId: ticket.PipelineId,
                RunId: recordId, Source: "jev", CostKnown: priced));
            reports.RequestRefresh();
            return recordId;
        }
        catch (Exception)
        {
            // Accounting failures must neither alter routing nor expose response bodies or secrets.
            logger.LogWarning("Failed to persist JEV API cost for project {Project} and ticket {Ticket}", slug, ticket.Id);
            return null;
        }
    }
}
