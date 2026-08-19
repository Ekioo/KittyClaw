using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapAutomations(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets/{ticketId:int}/automation-queue", async (
            string slug, int ticketId, AutomationQueueStore queue) =>
            Results.Ok(await queue.ListForTicketAsync(slug, ticketId)))
            .WithTags("Automations")
            .Produces<IReadOnlyList<AutomationQueueEntry>>();

        api.MapGet("/projects/{slug}/automations", async (string slug, AutomationStore store) =>
        {
            try
            {
                var (config, workspace, path, fileStamp) = await store.LoadWithStampAsync(slug);
                return Results.Ok(new { config, workspace, path, fileStamp });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        }).WithTags("Automations");

        api.MapPost("/projects/{slug}/automations/{automationId}/disable", async Task<IResult> (
            string slug, string automationId, AutomationStore store, AutomationEngine engine) =>
        {
            try
            {
                var result = await store.DisableAsync(slug, automationId);
                if (!result.Found)
                    return Results.NotFound(new { error = $"Automation '{automationId}' not found." });
                var reload = await engine.ReloadProjectAsync(slug);
                if (!reload.Success)
                    return Results.BadRequest(new
                    {
                        error = $"The automation was disabled, but the engine rejected the configuration: {reload.Error}",
                        previousRuntimeRetained = true,
                        fileStamp = result.FileStamp,
                    });
                return Results.Ok(new { automationId, enabled = false, fileStamp = result.FileStamp });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message, previousRuntimeRetained = true });
            }
        }).WithTags("Automations");

        api.MapDelete("/projects/{slug}/automations/{automationId}", async Task<IResult> (
            string slug, string automationId, AutomationStore store, AutomationEngine engine) =>
        {
            try
            {
                var result = await store.DeleteAsync(slug, automationId);
                if (!result.Found)
                    return Results.NotFound(new { error = $"Automation '{automationId}' not found." });
                var reload = await engine.ReloadProjectAsync(slug);
                if (!reload.Success)
                    return Results.BadRequest(new
                    {
                        error = $"The automation was deleted, but the engine rejected the configuration: {reload.Error}",
                        previousRuntimeRetained = true,
                        fileStamp = result.FileStamp,
                    });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message, previousRuntimeRetained = true });
            }
        }).WithTags("Automations");
    }
}
