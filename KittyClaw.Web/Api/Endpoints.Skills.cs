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
            var legacySkills = Directory.EnumerateDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                .Select(d => Path.GetFileName(d)!)
                .ToList();
            var projectSkillsDir = Path.Combine(dir, "skills");
            var projectSkills = Directory.Exists(projectSkillsDir)
                ? Directory.EnumerateDirectories(projectSkillsDir)
                    .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                    .Select(d => Path.GetFileName(d)!)
                : [];
            var skills = legacySkills.Concat(projectSkills).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Ok(skills);
        }).WithTags("Automations");

        api.MapGet("/projects/{slug}/project-skills", async (string slug, ProjectSkillService skills) =>
            Results.Ok(await skills.ListAsync(slug)))
            .WithTags("Skills");

        api.MapGet("/projects/{slug}/project-skills/{skillSlug}", async (
            string slug, string skillSlug, ProjectSkillService skills) =>
        {
            try
            {
                var instructions = await skills.ReadInstructionsAsync(slug, skillSlug);
                return instructions is null ? Results.NotFound() : Results.Ok(new { slug = skillSlug, instructions });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Skills");

        api.MapPost("/projects/{slug}/project-skills", async (
            string slug, CreateProjectSkillRequest req, ProjectSkillService skills) =>
        {
            try
            {
                var skill = await skills.CreateAsync(slug, req.Name, req.Instructions, req.Description);
                return Results.Created($"/api/projects/{slug}/project-skills/{skill.Slug}", skill);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Skills");

        api.MapPatch("/projects/{slug}/project-skills/{skillSlug}", async (
            string slug, string skillSlug, UpdateProjectSkillRequest req, ProjectSkillService skills) =>
        {
            try
            {
                var skill = await skills.UpdateAsync(slug, skillSlug, req.Name, req.Instructions, req.Description);
                return skill is null ? Results.NotFound() : Results.Ok(skill);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Skills");

        api.MapDelete("/projects/{slug}/project-skills/{skillSlug}", async (
            string slug, string skillSlug, ProjectSkillService skills) =>
        {
            try { return await skills.DeleteAsync(slug, skillSlug) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Skills");
    }
}
