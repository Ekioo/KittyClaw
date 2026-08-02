using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapColumnProcessors(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/columns/{columnId:int}/processor", async (
            string slug, int columnId, ColumnProcessorService processors) =>
        {
            var processor = await processors.GetAsync(slug, columnId);
            return processor is null ? Results.NotFound() : Results.Ok(processor);
        }).WithTags("Pipelines");

        api.MapPut("/projects/{slug}/columns/{columnId:int}/processor", async (
            string slug, int columnId, SaveColumnProcessorRequest req,
            ColumnProcessorService processors, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var processor = await processors.SaveAsync(
                    slug, columnId, req.Name, req.Mission, req.Model, req.Enabled, req.MaxTurns,
                    req.AvailableSkills, req.RecommendedSkills, req.RequiredSkills,
                    req.SelectionOrder, req.MaxAttempts, req.RetryBackoffSeconds,
                    req.DefaultTargetColumnId, req.TechnicalFailureColumnId, req.Routes, req.Prompt,
                    req.BeforeActions, req.AfterActions);
                notifier.NotifyProjectUpdated(slug);
                return Results.Ok(processor);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Pipelines");

        api.MapDelete("/projects/{slug}/columns/{columnId:int}/processor", async (
            string slug, int columnId, ColumnProcessorService processors, BoardUpdateNotifier notifier) =>
        {
            var deleted = await processors.DeleteAsync(slug, columnId);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Pipelines");
    }
}
