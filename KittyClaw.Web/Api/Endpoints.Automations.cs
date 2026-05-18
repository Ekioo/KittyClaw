using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapAutomations(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/automations", async (string slug, AutomationStore store) =>
        {
            try
            {
                var (config, workspace, path) = await store.LoadAsync(slug);
                return Results.Ok(new { config, workspace, path });
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

        api.MapPut("/projects/{slug}/automations", async (string slug, AutomationConfig config, AutomationStore store, AutomationEngine engine) =>
        {
            await store.SaveAsync(slug, config);
            await engine.ReloadProjectAsync(slug);
            return Results.NoContent();
        }).WithTags("Automations");

        api.MapPost("/projects/{slug}/automations/reload", async (string slug, AutomationEngine engine) =>
        {
            await engine.ReloadProjectAsync(slug);
            return Results.NoContent();
        }).WithTags("Automations");
    }
}
