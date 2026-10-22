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

        api.MapGet("/projects/{slug}/project-skills", HandleListProjectSkills).Produces<List<KittyClaw.Core.Models.ProjectSkill>>()
            .WithTags("Skills");

        api.MapGet("/projects/{slug}/project-skills/{skillSlug}", HandleGetProjectSkill).WithTags("Skills");

        api.MapPost("/projects/{slug}/project-skills", HandleCreateProjectSkill).WithTags("Skills");

        api.MapPatch("/projects/{slug}/project-skills/{skillSlug}", HandleUpdateProjectSkill).WithTags("Skills");

        api.MapDelete("/projects/{slug}/project-skills/{skillSlug}", HandleDeleteProjectSkill).WithTags("Skills");
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleListProjectSkills(string slug, ProjectSkillService skills)
    {
        return Results.Ok(await skills.ListAsync(slug));
    }

    internal static async Task<IResult> HandleGetProjectSkill(string slug, string skillSlug, ProjectSkillService skills)
    {
        try
        {
            var instructions = await skills.ReadInstructionsAsync(slug, skillSlug);
            return instructions is null ? Results.NotFound() : Results.Ok(new { slug = skillSlug, instructions });
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    internal static async Task<IResult> HandleCreateProjectSkill(string slug, CreateProjectSkillRequest req, ProjectSkillService skills)
    {
        try
        {
            var skill = await skills.CreateAsync(slug, req.Name, req.Instructions, req.Description);
            return Results.Created($"/api/projects/{slug}/project-skills/{skill.Slug}", skill);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    internal static async Task<IResult> HandleUpdateProjectSkill(string slug, string skillSlug, UpdateProjectSkillRequest req, ProjectSkillService skills)
    {
        try
        {
            var skill = await skills.UpdateAsync(slug, skillSlug, req.Name, req.Instructions, req.Description);
            return skill is null ? Results.NotFound() : Results.Ok(skill);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    internal static async Task<IResult> HandleDeleteProjectSkill(string slug, string skillSlug, ProjectSkillService skills)
    {
        try { return await skills.DeleteAsync(slug, skillSlug) ? Results.NoContent() : Results.NotFound(); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
}
