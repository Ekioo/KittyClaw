using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapColumnExecutions(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/column-executions", HandleListColumnExecutions).Produces<List<KittyClaw.Core.Models.ColumnExecution>>()
            .WithTags("Pipelines");

        api.MapPost("/projects/{slug}/column-executions/{executionId}/retry", HandleRetryColumnExecution).WithTags("Pipelines");

        api.MapPost("/projects/{slug}/column-executions/{executionId}/retry-memory", async (
            string slug, string executionId, ColumnProcessingEngine engine, BoardUpdateNotifier notifier) =>
        {
            if (!await engine.RetryMemoryAsync(slug, executionId)) return Results.NotFound(new { error = "No recoverable memory checkpoint." }) as IResult;
            notifier.NotifyProjectUpdated(slug);
            return Results.Accepted();
        }).WithTags("Pipelines");

        api.MapPost("/projects/{slug}/column-executions/{executionId}/cancel", HandleCancelColumnExecution).WithTags("Pipelines");
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleListColumnExecutions(string slug, int? ticketId, ColumnExecutionService executions)
    {
        return Results.Ok(await executions.ListAsync(slug, ticketId));
    }

    internal static async Task<IResult> HandleRetryColumnExecution(string slug, string executionId, ColumnExecutionService executions,
        ColumnProcessingEngine engine, BoardUpdateNotifier notifier)
    {
        var retried = await executions.RetryAsync(slug, executionId);
        if (!retried) return Results.NotFound();
        engine.Signal(slug);
        notifier.NotifyProjectUpdated(slug);
        return Results.Accepted();
    }

    internal static async Task<IResult> HandleCancelColumnExecution(string slug, string executionId, ColumnExecutionService executions,
        BoardUpdateNotifier notifier)
    {
        var cancelled = await executions.CancelAsync(slug, executionId);
        if (!cancelled) return Results.NotFound();
        notifier.NotifyProjectUpdated(slug);
        return Results.NoContent();
    }
}
