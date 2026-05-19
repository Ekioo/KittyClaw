using System.Text;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static readonly JsonSerializerOptions SseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void MapRuns(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/runs", (string slug, AgentRunRegistry reg) =>
            Results.Ok(reg.ActiveForProject(slug).Select(r => new
            {
                r.RunId, r.AgentName, r.SkillFile, r.TicketId, r.ConcurrencyGroup,
                r.StartedAt, r.SessionId, status = r.Status.ToString(),
            })))
            .WithTags("Runs");

        api.MapGet("/projects/{slug}/runs/{runId}", (string slug, string runId, AgentRunRegistry reg) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            return Results.Ok(new
            {
                run.RunId, run.AgentName, run.SkillFile, run.TicketId, run.ConcurrencyGroup,
                run.StartedAt, run.EndedAt, run.SessionId, run.ExitCode,
                status = run.Status.ToString(),
                events = run.SnapshotBuffer(),
            });
        }).WithTags("Runs");

        api.MapGet("/projects/{slug}/runs/{runId}/stream", async (string slug, string runId, string? since, HttpContext http, AgentRunRegistry reg, CancellationToken ct) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) { http.Response.StatusCode = 404; return; }
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // Optional ?since=<ISO timestamp> filter: replay only buffer events strictly after that
            // instant. Used when a chat drawer reattaches mid-run and already has all events up to
            // its latest persisted message — without this, the buffered events would re-render as
            // duplicates.
            DateTime? sinceUtc = null;
            if (!string.IsNullOrWhiteSpace(since)
                && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                sinceUtc = parsed.ToUniversalTime();
            }

            var queue = System.Threading.Channels.Channel.CreateUnbounded<StreamEvent>();
            void handler(StreamEvent ev) => queue.Writer.TryWrite(ev);
            run.OnEvent += handler;

            try
            {
                foreach (var ev in run.SnapshotBuffer())
                {
                    if (sinceUtc is not null && ev.At <= sinceUtc.Value) continue;
                    await WriteSseAsync(http.Response, ev, ct);
                }

                while (!ct.IsCancellationRequested && run.Status == AgentRunStatus.Running)
                {
                    while (queue.Reader.TryRead(out var ev))
                        await WriteSseAsync(http.Response, ev, ct);
                    try { await Task.Delay(200, ct); } catch { break; }
                }
                while (queue.Reader.TryRead(out var ev))
                    await WriteSseAsync(http.Response, ev, ct);
                await WriteSseRawAsync(http.Response, "event: end\ndata: {}\n\n", ct);
            }
            finally { run.OnEvent -= handler; }
        }).WithTags("Runs");

        api.MapPost("/projects/{slug}/runs/{runId}/steer", async (string slug, string runId, SteerRunRequest req, AgentRunRegistry reg, ChatService cs) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            if (run.Status != AgentRunStatus.Running) return Results.BadRequest(new { error = "Run is not active." });
            await run.SteeringQueue.Writer.WriteAsync(req.Text);
            if (!string.IsNullOrEmpty(run.ChatTarget))
                await cs.AppendAsync(slug, run.ChatTarget, "inject", req.Text);
            return Results.NoContent();
        }).WithTags("Runs");

        api.MapPost("/projects/{slug}/runs/{runId}/stop", (string slug, string runId, AgentRunRegistry reg) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            run.Cancellation.Cancel();
            return Results.NoContent();
        }).WithTags("Runs");

        api.MapPost("/projects/{slug}/runs/{runId}/retry", async (string slug, string runId,
            AgentRunRegistry reg, ProjectService ps, TicketService ts, ClaudeRunner runner) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            if (run.Status == AgentRunStatus.Running)
                return Results.BadRequest(new { error = "Run is still active." });
            if (reg.HasActiveInGroup(slug, run.ConcurrencyGroup))
                return Results.BadRequest(new { error = "An agent is already running in this group." });

            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            string? ticketTitle = null, ticketStatus = null;
            if (run.TicketId is int tid)
            {
                var ticket = await ts.GetTicketAsync(slug, tid);
                ticketTitle = ticket?.Title;
                ticketStatus = ticket?.Status;
            }

            var newRunId = Guid.NewGuid().ToString("N");
            var ctx = new ClaudeRunContext
            {
                ProjectSlug = slug,
                WorkspacePath = ps.ResolveWorkspacePath(project),
                AgentName = run.AgentName,
                SkillFile = run.SkillFile,
                TicketId = run.TicketId,
                TicketTitle = ticketTitle,
                TicketStatus = ticketStatus,
                ConcurrencyGroup = run.ConcurrencyGroup,
                Model = run.Model,
                FallbackModel = project.FallbackModel,
                RetryOnResumeFailure = true,
                PresetRunId = newRunId,
            };
            _ = runner.RunAsync(ctx, CancellationToken.None);
            return Results.Ok(new { runId = newRunId });
        }).WithTags("Runs");
    }

    private static async Task WriteSseAsync(HttpResponse res, StreamEvent ev, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(ev, SseJson);
        await WriteSseRawAsync(res, $"data: {payload}\n\n", ct);
    }

    private static async Task WriteSseRawAsync(HttpResponse res, string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await res.Body.WriteAsync(bytes, ct);
        await res.Body.FlushAsync(ct);
    }
}
