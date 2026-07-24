using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapGrok(RouteGroupBuilder api)
    {
        // Grok Build CLI detection is host-global (unlike Ollama, which is configured per
        // project), so this endpoint is not project-scoped. Returns an empty list when the
        // `grok` binary is not installed — the UI hides the Grok model group in that case.
        api.MapGet("/grok-models", async () =>
        {
            var models = await GrokCli.ListModelsAsync();
            return Results.Ok(models);
        }).WithTags("Grok");
    }
}
