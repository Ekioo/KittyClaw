using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapMistral(RouteGroupBuilder api)
    {
        api.MapGet("/mistral-models", async () =>
        {
            var models = await MistralCli.ListModelsAsync();
            return Results.Ok(models);
        }).WithTags("Mistral");
    }
}
