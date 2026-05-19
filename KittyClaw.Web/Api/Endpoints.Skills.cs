using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapSkills(RouteGroupBuilder api)
    {
        // Available skills for a project (scanned from WorkspacePath/.agents/<agent>/SKILL.md)
        api.MapGet("/projects/{slug}/skills", async (string slug, ProjectService ps) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();
            var dir = Path.Combine(ps.ResolveWorkspacePath(project), ".agents");
            if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<string>());
            var skills = Directory.EnumerateDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                .Select(d => Path.GetFileName(d)!)
                .OrderBy(s => s)
                .ToList();
            return Results.Ok(skills);
        }).WithTags("Automations");
    }
}
