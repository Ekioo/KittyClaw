using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapCodex(RouteGroupBuilder api)
    {
        api.MapGet("/codex-models", async () =>
        {
            var models = await CodexCli.ListModelsAsync();
            return Results.Ok(models);
        }).WithTags("Codex");
    }
}
