using System.Text;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static readonly JsonSerializerOptions SseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void MapRuns(RouteGroupBuilder api)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("KITTYCLAW_ENABLE_QA_ENDPOINTS"), "1", StringComparison.Ordinal))
        {
            api.MapPost("/projects/{slug}/qa/chat/fail-next-start", (string slug) =>
            {
                QaFailNextChatStarts[slug] = 0;
                return Results.Ok(new { armed = true });
            }).ExcludeFromDescription();

            api.MapPost("/projects/{slug}/qa/runs", (string slug, QaSeedRunsRequest req, AgentRunRegistry reg) =>
            {
                var ticketIds = req.TicketIds.Select(int.Parse).ToArray();
                if (ticketIds.Length == 0 || req.Count is < 1 or > 500)
                    return Results.BadRequest(new { error = "ticketIds and count (1..500) are required" });

                for (var i = 0; i < req.Count; i++)
                {
                    reg.Register(new AgentRun
                    {
                        RunId = $"qa-{Guid.NewGuid():N}",
                        ProjectSlug = slug,
                        TicketId = ticketIds[i % ticketIds.Length],
                        AgentName = $"qa-agent-{i + 1}",
                        SkillFile = "qa",
                        ConcurrencyGroup = $"qa-load-{i + 1}",
                        StartedAt = DateTime.UtcNow,
                    });
                }

                return Results.Ok(new { count = req.Count });
            }).ExcludeFromDescription();

            api.MapGet("/projects/{slug}/qa/runs/latest-input-images", (string slug, AgentRunRegistry reg) =>
            {
                var run = reg.AllForProject(slug)
                    .OrderByDescending(r => r.StartedAt)
                    .FirstOrDefault(r => r.ChatTarget is not null);
                return run is null
                    ? Results.NotFound()
                    : Results.Ok(new
                    {
                        count = run.InputImagePaths.Count,
                        extensions = run.InputImagePaths.Select(Path.GetExtension).ToArray(),
                        hashes = run.InputImageHashes,
                    });
            }).ExcludeFromDescription();
        }

        api.MapGet("/projects/{slug}/runs", (string slug, AgentRunRegistry reg) =>
            Results.Ok(reg.ActiveForProject(slug).Select(r => new
            {
                r.RunId, r.AgentName, r.SkillFile, r.TicketId, r.ConcurrencyGroup,
                r.StartedAt, r.SessionId, r.WorkingDirectory, status = r.Status.ToString(),
                r.CliVersion,
                r.TotalTokens, r.TotalCostUsd, r.CostIsEstimated,
            })))
            .WithTags("Runs");

        // Observability (feature #2, ticket #98): list the concurrency groups currently locked for a
        // project — i.e. groups that have at least one Running run. Without this a zombie lock (a hung
        // run that never completes) is invisible until KittyClaw restarts. The lock holder is the
        // earliest-started active run in the group (that is the one HasActiveInGroup keys on).
        api.MapGet("/projects/{slug}/concurrency-groups", (string slug, AgentRunRegistry reg) =>
            Results.Ok(reg.ActiveForProject(slug)
                .GroupBy(r => r.ConcurrencyGroup)
                .Select(g =>
                {
                    var holder = g.OrderBy(r => r.StartedAt).First();
                    return new
                    {
                        group = g.Key,
                        runId = holder.RunId,
                        agentName = holder.AgentName,
                        ticketId = holder.TicketId,
                        lockedAt = holder.StartedAt,
                        lastActivityAt = holder.LastActivityAt,
                        lockTimeoutMinutes = holder.LockTimeoutMinutes,
                        activeRuns = g.Count(),
                    };
                })
                .OrderBy(x => x.group)))
            .WithTags("Runs");

        api.MapGet("/projects/{slug}/runs/{runId}", (string slug, string runId, AgentRunRegistry reg) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            return Results.Ok(new
            {
                run.RunId, run.AgentName, run.SkillFile, run.TicketId, run.ConcurrencyGroup,
                run.StartedAt, run.EndedAt, run.SessionId, run.ExitCode,
                run.WorkingDirectory,
                run.CliVersion,
                status = run.Status.ToString(),
                run.InputTokens, run.OutputTokens, run.CacheReadTokens, run.CacheWriteTokens,
                run.TotalTokens, run.TotalCostUsd, run.CostIsEstimated,
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

        api.MapPost("/projects/{slug}/runs/{runId}/steer", async (string slug, string runId, SteerRunRequest req, AgentRunRegistry reg, ChatService cs, string? model) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            if (run.Status != AgentRunStatus.Running) return Results.BadRequest(new { error = "Run is not active." });
            await run.SteeringQueue.Writer.WriteAsync(req.Text);
            var targetModel = model ?? run.Model; // Use provided model or the run's current model
            if (!string.IsNullOrEmpty(run.ChatTarget))
                await cs.AppendAsync(slug, run.ChatTarget, "inject", req.Text, targetModel);
            return Results.NoContent();
        }).WithTags("Runs");

        api.MapPost("/projects/{slug}/runs/{runId}/stop", async (string slug, string runId, AgentRunRegistry reg, CancellationToken ct) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            await AutomationEngine.CancelAndWaitForRunsAsync([run], ct);
            return Results.NoContent();
        }).WithTags("Runs");

        api.MapGet("/projects/{slug}/runs/{runId}/evidence", (string slug, string runId, EvidenceStore store, AgentRunRegistry reg) =>
        {
            var run = reg.Get(runId);
            if (run is not null && run.ProjectSlug != slug) return Results.NotFound();
            var evidence = store.LoadRun(slug, runId);
            if (evidence is not null && !string.Equals(evidence.ProjectSlug, slug, StringComparison.Ordinal))
                return Results.NotFound();
            if (evidence is null) return Results.NotFound();
            EvidenceStore.ApplyStaleness(evidence, EvidenceStore.DefaultStalenessThreshold);
            return Results.Ok(evidence);
        }).WithTags("Runs");

        api.MapPost("/projects/{slug}/runs/{runId}/retry", async (string slug, string runId,
            AgentRunRegistry reg, ProjectService ps, TicketService ts, AgentRunner runner) =>
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
            var routing = ModelRouting.Resolve(run.Model, project.LocalModelBaseUrl);
            // Same cross-provider fallback resolution as ActionExecutor: an unusable fallback
            // is dropped rather than failing the retry.
            var fallbackModel = project.FallbackModel;
            var fallbackRouting = fallbackModel is null ? null : ModelRouting.Resolve(fallbackModel, project.LocalModelBaseUrl);
            if (fallbackRouting?.Error is not null) { fallbackModel = null; fallbackRouting = null; }
            var target = routing.ToTarget(run.Model);
            var fallbackTarget = fallbackModel is null || fallbackRouting is null
                ? null
                : fallbackRouting.ToTarget(fallbackModel);
            var ctx = new AgentRunContext
            {
                ProjectSlug = slug,
                WorkspacePath = ps.ResolveWorkspacePath(project),
                AgentName = run.AgentName,
                SkillFile = run.SkillFile,
                TicketId = run.TicketId,
                TicketTitle = ticketTitle,
                TicketStatus = ticketStatus,
                ConcurrencyGroup = run.ConcurrencyGroup,
                Target = target,
                FallbackTarget = fallbackTarget,
                RetryOnResumeFailure = true,
                PresetRunId = newRunId,
                MaxRunDuration = TimeSpan.FromMinutes(30),
            };
            _ = runner.RunAsync(ctx, CancellationToken.None);
            return Results.Ok(new { runId = newRunId });
        }).WithTags("Runs");
    }

    private sealed record QaSeedRunsRequest(IReadOnlyList<string> TicketIds, int Count);

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
