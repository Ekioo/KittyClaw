using KittyClaw.Core.Services;
using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapProjects(RouteGroupBuilder api)
    {
        api.MapGet("/projects", async (ProjectService ps) =>
            Results.Ok(await ps.ListProjectsAsync()))
            .WithTags("Projects");

        api.MapPost("/projects", async (CreateProjectRequest req, ProjectService ps) =>
        {
            var project = await ps.CreateProjectAsync(req.Name);
            return Results.Created($"/api/projects/{project.Slug}", project);
        }).WithTags("Projects");

        api.MapGet("/projects/{slug}", async (string slug, ProjectService ps) =>
        {
            var project = await ps.GetProjectAsync(slug);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapDelete("/projects/{slug}", async (string slug, ProjectService ps) =>
        {
            var deleted = await ps.DeleteProjectAsync(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Projects");

        api.MapPatch("/projects/{slug}", async (string slug, UpdateProjectRequest req, ProjectService ps) =>
        {
            try
            {
                var project = await ps.UpdateProjectAsync(
                    slug, req.WorkspacePath, req.FallbackModel, req.UpdateFallbackModel,
                    req.WorktreesEnabled, req.IntegrationBranch, req.RepositoryPath);
                return project is null ? Results.NotFound() : Results.Ok(project);
            }
            catch (InvalidOperationException ex)
            {
                // Workspace validation (§2.6): relative / ".." / drive root / system dirs.
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Projects")
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPost("/projects/{slug}/pause", async (string slug, ProjectService ps, AgentRunRegistry runs, CancellationToken ct) =>
        {
            var project = await ps.TogglePauseAsync(slug);
            if (project?.IsPaused == true)
            {
                var active = runs.ActiveForProject(slug).ToArray();
                await AutomationEngine.CancelAndWaitForRunsAsync(active, ct);
            }
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapPatch("/projects/{slug}/local-model", async (string slug, SaveLocalModelConfigRequest req, ProjectService ps) =>
        {
            var project = await ps.SaveLocalModelConfigAsync(slug, req.LocalModelBaseUrl, req.LocalModelName);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapGet("/projects/{slug}/rtk", async (string slug, RtkIntegrationService rtk) =>
        {
            var status = await rtk.GetStatusAsync(slug);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithTags("Projects");

        api.MapPatch("/projects/{slug}/rtk", async (
            string slug,
            SaveRtkConfigRequest req,
            ProjectService ps,
            RtkIntegrationService rtk,
            CostReportService costReports) =>
        {
            var project = await ps.SaveRtkEnabledAsync(slug, req.Enabled);
            if (project is null) return Results.NotFound();
            costReports.RequestRefresh();
            return Results.Ok(await rtk.GetStatusAsync(slug));
        }).WithTags("Projects");

        api.MapGet("/projects/{slug}/git", async (
            string slug,
            GitRepositoryInitializationService git) =>
        {
            var status = await git.GetStatusAsync(slug);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithTags("Projects");

        api.MapPost("/projects/{slug}/git/init", async (
            string slug,
            GitRepositoryInitializationService git) =>
        {
            try
            {
                var result = await git.InitializeAsync(slug);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (GitRepositoryAlreadyExistsException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Projects")
        .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
