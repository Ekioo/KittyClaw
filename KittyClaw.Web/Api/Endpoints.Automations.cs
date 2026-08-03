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

        // baseStamp: fileStamp returned by the GET. When it matches the current file, the save is
        // applied verbatim (deletions honored). When it is stale or omitted, automations present on
        // disk but missing from the payload are preserved instead of being silently erased (#115).
        api.MapPut("/projects/{slug}/automations", async Task<IResult> (string slug, AutomationConfig config, string? baseStamp, AutomationStore store, AutomationEngine engine) =>
        {
            var result = await store.SaveAsync(slug, config, string.IsNullOrEmpty(baseStamp) ? null : baseStamp);
            var reload = await engine.ReloadProjectAsync(slug);
            if (!reload.Success)
                return Results.BadRequest(new
                {
                    error = $"La configuration a été enregistrée, mais le moteur l'a rejetée : {reload.Error}",
                    previousRuntimeRetained = true,
                    fileStamp = result.FileStamp,
                });
            return Results.Ok(new
            {
                config = result.Config,
                fileStamp = result.FileStamp,
                preservedIds = result.PreservedIds,
                diverged = result.Diverged,
            });
        }).WithTags("Automations");

        api.MapPost("/projects/{slug}/automations/reload", async Task<IResult> (string slug, AutomationEngine engine) =>
        {
            var reload = await engine.ReloadProjectAsync(slug);
            return reload.Success
                ? Results.NoContent()
                : Results.BadRequest(new
                {
                    error = $"Le moteur a rejeté la configuration : {reload.Error}",
                    previousRuntimeRetained = true,
                });
        }).WithTags("Automations");
    }
}
