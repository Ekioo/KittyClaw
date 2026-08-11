using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapWorktreeMerges(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/projects/{slug}/worktree-merges").WithTags("Worktree merges");
        group.MapGet("/", async (string slug, WorktreeMergeQueueService queue) => Results.Ok(await queue.ListAsync(slug)));
        group.MapPost("/", async (string slug, EnqueueWorktreeMergeRequest req, WorktreeMergeQueueService queue, CancellationToken ct) =>
        {
            try { return Results.Ok(await queue.EnqueueAsync(slug, req.TicketId, ct)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        group.MapPost("/process-next", async (string slug, WorktreeMergeQueueService queue, CancellationToken ct) =>
            Results.Ok(await queue.ProcessNextAsync(slug, ct)));
        group.MapPost("/{requestId:long}/resume", async (string slug, long requestId, WorktreeMergeQueueService queue, CancellationToken ct) =>
        {
            var result = await queue.ResumeAsync(slug, requestId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}

public sealed record EnqueueWorktreeMergeRequest(int TicketId);
