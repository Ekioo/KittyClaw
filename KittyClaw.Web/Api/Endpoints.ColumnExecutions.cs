using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapColumnExecutions(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/column-executions", async (
            string slug, int? ticketId, ColumnExecutionService executions) =>
            Results.Ok(await executions.ListAsync(slug, ticketId)))
            .WithTags("Pipelines");

        api.MapPost("/projects/{slug}/column-executions/{executionId}/retry", async (
            string slug, string executionId, ColumnExecutionService executions,
            ColumnProcessingEngine engine, BoardUpdateNotifier notifier) =>
        {
            var retried = await executions.RetryAsync(slug, executionId);
            if (!retried) return Results.NotFound();
            engine.Signal(slug);
            notifier.NotifyProjectUpdated(slug);
            return Results.Accepted();
        }).WithTags("Pipelines");

        api.MapPost("/projects/{slug}/column-executions/{executionId}/cancel", async (
            string slug, string executionId, ColumnExecutionService executions,
            BoardUpdateNotifier notifier) =>
        {
            var cancelled = await executions.CancelAsync(slug, executionId);
            if (!cancelled) return Results.NotFound();
            notifier.NotifyProjectUpdated(slug);
            return Results.NoContent();
        }).WithTags("Pipelines");
    }
}
