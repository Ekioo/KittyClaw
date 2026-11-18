using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapCopilot(RouteGroupBuilder api)
    {
        api.MapGet("/copilot-models", async () =>
        {
            var models = await CopilotCli.ListModelsAsync();
            return Results.Ok(models);
        }).WithTags("GitHub Copilot");
    }
}
