using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapWorkflowMigrations(RouteGroupBuilder api)
    {
        api.MapPost("/projects/{slug}/workflow-migrations/analyze", (
            string slug, AnalyzeWorkflowMigrationRequest? request, WorkflowMigrationPlanner planner) =>
            Results.Accepted(value: new { jobId = planner.StartAnalysis(slug, request?.Mode ?? "migration", request?.Brief) }))
            .WithTags("Workflow migrations");

        api.MapPost("/projects/{slug}/workflow-migrations/refine", (
            string slug, RefineWorkflowMigrationRequest request, WorkflowMigrationPlanner planner) =>
        {
            if (string.IsNullOrWhiteSpace(request.Instruction))
                return Results.BadRequest(new { error = "An instruction is required." });
            return Results.Accepted(value: new
            {
                jobId = planner.StartRefinement(slug, request.Plan, request.Instruction,
                    request.Phase, request.PipelineIndex),
            });
        }).WithTags("Workflow migrations");

        api.MapPost("/projects/{slug}/workflow-migrations/apply", (
            string slug, ApplyWorkflowMigrationRequest request, WorkflowMigrationPlanner planner) =>
            Results.Accepted(value: new
            {
                jobId = planner.StartApplication(slug, request.Plan, request.IsProjectOnboarding),
            }))
            .WithTags("Workflow migrations");

        api.MapGet("/projects/{slug}/workflow-migrations/jobs/{jobId}", (
            string slug, string jobId, WorkflowMigrationPlanner planner) =>
        {
            var job = planner.Get(slug, jobId);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }).WithTags("Workflow migrations");
    }
}

public sealed record AnalyzeWorkflowMigrationRequest(string Mode = "migration", string? Brief = null);

public sealed record RefineWorkflowMigrationRequest(
    WorkflowMigrationPlan Plan,
    string Instruction,
    string Phase,
    int? PipelineIndex);

public sealed record ApplyWorkflowMigrationRequest(
    WorkflowMigrationPlan Plan,
    bool IsProjectOnboarding);
