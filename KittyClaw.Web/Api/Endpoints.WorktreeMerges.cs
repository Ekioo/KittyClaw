using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapWorktreeMerges(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/projects/{slug}/worktree-merges").WithTags("Worktree merges");
        group.MapGet("/", HandleListWorktreeMerges).Produces<IReadOnlyList<WorktreeMergeRequest>>();
        group.MapGet("/{requestId:long}", HandleGetWorktreeMerge);
        group.MapPost("/", HandleEnqueueWorktreeMerge);
        group.MapPost("/process-next", HandleProcessNextWorktreeMerge);
        group.MapPost("/{requestId:long}/resume", HandleResumeWorktreeMerge);
        group.MapPost("/{requestId:long}/retry-synchronization", HandleRetryWorktreeSynchronization);
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleListWorktreeMerges(string slug, WorktreeMergeQueueService queue)
    {
        return Results.Ok(await queue.ListAsync(slug));
    }

    internal static async Task<IResult> HandleGetWorktreeMerge(string slug, long requestId, WorktreeMergeQueueService queue)
    {
        var result = await queue.GetAsync(slug, requestId);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    internal static async Task<IResult> HandleEnqueueWorktreeMerge(string slug, EnqueueWorktreeMergeRequest req, WorktreeMergeQueueService queue, CancellationToken ct)
    {
        try { return Results.Ok(await queue.EnqueueAsync(slug, req.TicketId, ct)); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    internal static async Task<IResult> HandleProcessNextWorktreeMerge(string slug, WorktreeMergeQueueService queue, CancellationToken ct)
    {
        var pending = await queue.ProcessNextAsync(slug, ct);
        if (pending is not null) return Results.Ok(pending);
        await queue.RecoverTerminalWorktreesAsync(slug, ct);
        return Results.Ok(await queue.ProcessNextAsync(slug, ct));
    }

    internal static async Task<IResult> HandleResumeWorktreeMerge(string slug, long requestId, WorktreeMergeQueueService queue, CancellationToken ct)
    {
        var result = await queue.ResumeAsync(slug, requestId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    internal static async Task<IResult> HandleRetryWorktreeSynchronization(string slug, long requestId, WorktreeMergeQueueService queue, CancellationToken ct)
    {
        var result = await queue.RetrySynchronizationAsync(slug, requestId, ct);
        return result is null
            ? Results.Conflict(new { error = "The synchronization request is not in a retryable state." })
            : Results.Ok(result);
    }
}

public sealed record EnqueueWorktreeMergeRequest(int TicketId);
