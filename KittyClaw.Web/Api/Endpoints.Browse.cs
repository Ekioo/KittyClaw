namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapBrowse(RouteGroupBuilder api)
    {
        // Capability probe — lets the UI hide the browse button when no picker is available
        // (e.g. cloud-hosted deployment where the server has no desktop).
        // [FromServices] is required: IFolderPicker is only registered in DI on Windows, and
        // for an unregistered type minimal APIs infer the parameter as a request body — which
        // MapGet forbids, so the host throws at startup on macOS/Linux (GitHub issue #2).
        // The nullable parameter resolves to null when nothing is registered.
        api.MapGet("/browse/capabilities", ([Microsoft.AspNetCore.Mvc.FromServices] KittyClaw.Core.Platform.IFolderPicker? picker) =>
            Results.Ok(new { folderPicker = picker?.IsAvailable == true }))
            .WithTags("Browse");

        api.MapPost("/browse/folder", async (BrowseFolderRequest? req, [Microsoft.AspNetCore.Mvc.FromServices] KittyClaw.Core.Platform.IFolderPicker? picker, CancellationToken ct) =>
        {
            if (picker is null || !picker.IsAvailable)
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            try
            {
                var path = await picker.PickFolderAsync(req?.InitialPath, ct);
                return string.IsNullOrEmpty(path)
                    ? Results.NoContent()
                    : Results.Ok(new { path });
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        }).WithTags("Browse");
    }
}
