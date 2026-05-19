using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static readonly HashSet<string> AllowedImageExts = ["png", "jpeg", "jpg", "gif", "webp", "svg"];

    private static void MapImages(RouteGroupBuilder api)
    {
        api.MapPost("/images", async (HttpRequest req, ProjectService ps) =>
        {
            if (!req.HasFormContentType || req.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file provided" });
            var file = req.Form.Files[0];
            if (!file.ContentType.StartsWith("image/"))
                return Results.BadRequest(new { error = "File must be an image" });
            var ext = file.ContentType.Split('/')[1].Split('+')[0];
            if (!AllowedImageExts.Contains(ext)) ext = "png";
            var filename = $"{Guid.NewGuid():N}.{ext}";
            var uploadsDir = Path.Combine(ps.DataDir, "uploads");
            Directory.CreateDirectory(uploadsDir);
            await using var fs = File.Create(Path.Combine(uploadsDir, filename));
            await file.CopyToAsync(fs);
            return Results.Ok(new { url = $"/uploads/{filename}" });
        }).WithTags("Images").DisableAntiforgery();
    }
}
