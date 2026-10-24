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

        api.MapPost("/projects", HandleCreateProject).Produces<KittyClaw.Core.Models.Project>(StatusCodes.Status201Created).WithTags("Projects");

        api.MapGet("/projects/{slug}", HandleGetProject).WithTags("Projects");

        api.MapDelete("/projects/{slug}", async (string slug, ProjectService ps) =>
        {
            var deleted = await ps.DeleteProjectAsync(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Projects");

        api.MapPatch("/projects/{slug}", HandleUpdateProject).WithTags("Projects")
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPost("/projects/{slug}/pause", HandleToggleProjectPause).WithTags("Projects");

        api.MapPatch("/projects/{slug}/local-model", HandleConfigureLocalModel).WithTags("Projects");

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

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleCreateProject(CreateProjectRequest req, ProjectService ps)
    {
        var project = await ps.CreateProjectAsync(req.Name);
        return Results.Created($"/api/projects/{project.Slug}", project);
    }

    internal static async Task<IResult> HandleGetProject(string slug, ProjectService ps)
    {
        var project = await ps.GetProjectAsync(slug);
        return project is null ? Results.NotFound() : Results.Ok(project);
    }

    internal static async Task<IResult> HandleUpdateProject(string slug, UpdateProjectRequest req, ProjectService ps)
    {
        try
        {
            var project = await ps.UpdateProjectAsync(
                slug, req.WorkspacePath, req.FallbackModel, req.UpdateFallbackModel,
                req.WorktreesEnabled, req.IntegrationBranch, req.RepositoryPath, req.BoundaryEnforcement);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }
        catch (InvalidOperationException ex)
        {
            // Workspace validation (§2.6): relative / ".." / drive root / system dirs.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> HandleToggleProjectPause(string slug, ProjectService ps, AgentRunRegistry runs, CancellationToken ct)
    {
        var project = await ps.TogglePauseAsync(slug);
        if (project?.IsPaused == true)
        {
            var active = runs.ActiveForProject(slug).ToArray();
            await AgentRunLifecycle.CancelAndWaitForRunsAsync(active, ct);
        }
        return project is null ? Results.NotFound() : Results.Ok(project);
    }

    internal static async Task<IResult> HandleConfigureLocalModel(string slug, SaveLocalModelConfigRequest req, ProjectService ps)
    {
        var project = await ps.SaveLocalModelConfigAsync(slug, req.LocalModelBaseUrl, req.LocalModelName);
        return project is null ? Results.NotFound() : Results.Ok(project);
    }
}
