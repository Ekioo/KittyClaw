using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapEngine(RouteGroupBuilder api) =>
        api.MapGet("/engine/health", HandleGetEngineHealth).WithTags("Engine");

    internal static async Task<IResult> HandleGetEngineHealth(ProjectService projects, ColumnProcessingEngine engine)
    {
        var now = DateTime.UtcNow;
        var list = await projects.ListProjectsAsync();
        return Results.Ok(new
        {
            engine = "column-processing",
            startedAt = engine.StartedAt,
            lastTickAt = engine.LastTickAt,
            lastTickAgeSeconds = engine.LastTickAt is DateTime tick
                ? Math.Round((now - tick).TotalSeconds, 1) : (double?)null,
            projects = list.Select(project => new { slug = project.Slug, isPaused = project.IsPaused }),
        });
    }
}
