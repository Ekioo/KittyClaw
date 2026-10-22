using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapAutomations(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets/{ticketId:int}/automation-queue", HandleGetTicketAutomationQueue)
            .WithTags("Automations")
            .Produces<IReadOnlyList<AutomationQueueEntry>>();

        api.MapGet("/projects/{slug}/automations", HandleGetAutomations).WithTags("Automations");

        api.MapPost("/projects/{slug}/automations/{automationId}/disable", HandleDisableAutomation).WithTags("Automations");

        api.MapDelete("/projects/{slug}/automations/{automationId}", HandleDeleteAutomation).WithTags("Automations");
    }

    // MCP-only configuration writes preserve concurrent rules through AutomationStore.
    internal static async Task<IResult> HandleSaveAutomations(
        string slug, SaveAutomationsRequest request, AutomationStore store, AutomationEngine engine)
    {
        try
        {
            if (request.Config is null || request.Config.Automations is null || request.Config.Automations.Any(a =>
                    a is null || string.IsNullOrWhiteSpace(a.Id) || a.Trigger is null || a.Actions is null || a.Conditions is null))
                return Results.BadRequest(new { error = "Automations require an id, trigger, conditions and actions." });
            if (request.Config.Automations.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != request.Config.Automations.Count)
                return Results.BadRequest(new { error = "Automation ids must be unique." });
            var saved = await store.SaveAsync(slug, request.Config, request.BaseStamp);
            var reload = await engine.ReloadProjectAsync(slug);
            if (!reload.Success)
                return Results.BadRequest(new
                {
                    error = $"Configuration was saved, but the engine rejected it: {reload.Error}",
                    previousRuntimeRetained = true,
                    saved.FileStamp,
                    saved.PreservedIds,
                    saved.Diverged,
                });
            return Results.Ok(saved);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message, previousRuntimeRetained = true }); }
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleGetAutomations(string slug, AutomationStore store)
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
    }

    internal static async Task<IResult> HandleDisableAutomation(string slug, string automationId, AutomationStore store, AutomationEngine engine)
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
    }

    internal static async Task<IResult> HandleDeleteAutomation(string slug, string automationId, AutomationStore store, AutomationEngine engine)
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
    }

    internal static async Task<IResult> HandleGetTicketAutomationQueue(string slug, int ticketId, AutomationQueueStore queue)
    {
        return Results.Ok(await queue.ListForTicketAsync(slug, ticketId));
    }
}
