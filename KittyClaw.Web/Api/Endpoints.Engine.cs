using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapEngine(RouteGroupBuilder api)
    {
        // Anti-silent-outage observability (ticket #114): per project, how many scheduled tasks
        // are actually registered in the engine, whether any are overdue, and when an automation
        // last fired. lastTickAgeSeconds staying high means the tick loop itself is dead or hung.
        api.MapGet("/engine/health", async (ProjectService projects, AutomationEngine engine) =>
        {
            var now = DateTime.UtcNow;
            var list = await projects.ListProjectsAsync();
            var items = list.Select(p =>
            {
                var h = engine.GetProjectHealth(p.Slug);
                return new
                {
                    slug = p.Slug,
                    isPaused = p.IsPaused,
                    loaded = h is not null,
                    automations = h?.AutomationCount ?? 0,
                    enabled = h?.EnabledCount ?? 0,
                    scheduledRegistered = h?.ScheduledCount ?? 0,
                    nextRunAt = h?.NextRunAt,
                    overdue = h?.EffectiveOverdueCount(p.IsPaused) ?? 0,
                    lastFiredAt = h?.LastFiredAt,
                    lastFiredAutomationId = h?.LastFiredAutomationId,
                };
            }).ToList();
            return Results.Ok(new
            {
                startedAt = engine.StartedAt,
                lastTickAt = engine.LastTickAt,
                lastTickAgeSeconds = engine.LastTickAt is DateTime t
                    ? Math.Round((now - t).TotalSeconds, 1)
                    : (double?)null,
                projects = items,
            });
        }).WithTags("Engine");
    }
}
