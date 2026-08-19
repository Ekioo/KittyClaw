using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapDeepSeek(RouteGroupBuilder api)
    {
        api.MapGet("/deepseek-models", async () =>
        {
            var models = await DeepSeekModelCatalog.ListModelsAsync();
            return Results.Ok(models);
        }).WithTags("DeepSeek");
    }
}
