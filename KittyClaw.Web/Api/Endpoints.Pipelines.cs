using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapPipelines(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/pipelines", async (string slug, PipelineService pipelines) =>
            Results.Ok(await pipelines.ListAsync(slug)))
            .WithTags("Pipelines");

        api.MapPost("/projects/{slug}/pipelines", async (
            string slug, CreatePipelineRequest req, PipelineService pipelines, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var pipeline = await pipelines.CreateAsync(slug, req.Name);
                notifier.NotifyProjectUpdated(slug);
                return Results.Created($"/api/projects/{slug}/pipelines/{pipeline.Id}", pipeline);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Pipelines");

        api.MapPatch("/projects/{slug}/pipelines/{pipelineId:int}", async (
            string slug, int pipelineId, UpdatePipelineRequest req,
            PipelineService pipelines, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var pipeline = await pipelines.UpdateAsync(slug, pipelineId, req.Name);
                if (pipeline is not null) notifier.NotifyProjectUpdated(slug);
                return pipeline is null ? Results.NotFound() : Results.Ok(pipeline);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Pipelines");
    }
}
