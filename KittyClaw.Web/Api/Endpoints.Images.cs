using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private static void MapImages(RouteGroupBuilder api)
    {
        api.MapPost("/images", async (HttpRequest req, ProjectService ps) =>
        {
            if (!req.HasFormContentType || req.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file provided" });
            var file = req.Form.Files[0];
            if (file.Length is 0 or > MaxImageBytes)
                return Results.BadRequest(new { error = $"File must be between 1 byte and {MaxImageBytes / (1024 * 1024)} MB" });

            // Sniff the real format instead of trusting client ContentType. SVG is deliberately
            // rejected: it is active content (scripts run when navigated to under /uploads/).
            await using var stream = file.OpenReadStream();
            var header = new byte[12];
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
            var ext = SniffImageExtension(header.AsSpan(0, read));
            if (ext is null)
                return Results.BadRequest(new { error = "File must be a PNG, JPEG, GIF or WebP image" });

            var filename = $"{Guid.NewGuid():N}.{ext}";
            var uploadsDir = Path.Combine(ps.DataDir, "uploads");
            Directory.CreateDirectory(uploadsDir);
            await using var fs = File.Create(Path.Combine(uploadsDir, filename));
            fs.Write(header, 0, read);
            await stream.CopyToAsync(fs);
            return Results.Ok(new { url = $"/uploads/{filename}" });
        }).WithTags("Images").DisableAntiforgery();
    }

    private static string? SniffImageExtension(ReadOnlySpan<byte> h) => h switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "png",
        [0xFF, 0xD8, 0xFF, ..] => "jpg",
        [0x47, 0x49, 0x46, 0x38, ..] => "gif", // GIF8
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "webp", // RIFF....WEBP
        _ => null,
    };
}
