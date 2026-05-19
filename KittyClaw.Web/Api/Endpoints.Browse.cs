namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapBrowse(RouteGroupBuilder api)
    {
        // Capability probe — lets the UI hide the browse button when no picker is available
        // (e.g. cloud-hosted deployment where the server has no desktop).
        api.MapGet("/browse/capabilities", (KittyClaw.Core.Platform.IFolderPicker? picker) =>
            Results.Ok(new { folderPicker = picker?.IsAvailable == true }))
            .WithTags("Browse");

        api.MapPost("/browse/folder", async (BrowseFolderRequest? req, KittyClaw.Core.Platform.IFolderPicker? picker, CancellationToken ct) =>
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
