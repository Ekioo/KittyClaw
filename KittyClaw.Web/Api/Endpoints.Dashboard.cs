using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapDashboard(RouteGroupBuilder api)
    {
        // Dashboard (folder-per-tile layout: .dashboard/<tileSlug>/{tile.yaml,script.*,output.*})

        api.MapGet("/projects/{slug}/dashboard/tiles", async (string slug, DashboardService ds) =>
            Results.Ok(await ds.GetTilesAsync(slug)))
            .WithTags("Dashboard");

        api.MapPost("/projects/{slug}/dashboard/tiles", async (string slug, AddTileRequest req, DashboardService ds) =>
        {
            var tile = await ds.AddTileAsync(slug, req.TileSlug);
            return Results.Created($"/api/projects/{slug}/dashboard/tiles/{req.TileSlug}", tile);
        }).WithTags("Dashboard");

        // Removes the tile from the layout AND deletes the entire .dashboard/<tileSlug>/ folder.
        api.MapDelete("/projects/{slug}/dashboard/tiles/{tileSlug}", async (string slug, string tileSlug, ProjectService ps, DashboardService ds) =>
        {
            var removed = await ds.RemoveTileAsync(slug, tileSlug);
            if (!removed) return Results.NotFound();
            var project = await ps.GetProjectAsync(slug);
            if (project is not null)
                ds.DeleteTileFolder(ps.ResolveWorkspacePath(project), tileSlug);
            return Results.NoContent();
        }).WithTags("Dashboard");

        api.MapPatch("/projects/{slug}/dashboard/tiles/{tileSlug}/position", async (string slug, string tileSlug, MoveTileRequest req, DashboardService ds) =>
        {
            var tile = await ds.MoveTileAsync(slug, tileSlug, req.X, req.Y);
            return tile is null ? Results.NotFound() : Results.Ok(tile);
        }).WithTags("Dashboard");

        api.MapPatch("/projects/{slug}/dashboard/tiles/{tileSlug}/size", async (string slug, string tileSlug, ResizeTileRequest req, DashboardService ds) =>
        {
            var tile = await ds.ResizeTileAsync(slug, tileSlug, req.Width, req.Height);
            return tile is null ? Results.NotFound() : Results.Ok(tile);
        }).WithTags("Dashboard");

        // Returns the rendered output content of a tile (text/plain). 404 if no output.* file.
        api.MapGet("/projects/{slug}/dashboard/tiles/{tileSlug}/output", async (string slug, string tileSlug, ProjectService ps, DashboardService ds) =>
        {
            var workspace = await ResolveDashboardWorkspaceAsync(slug, ps);
            if (workspace is null) return Results.NotFound();
            if (!IsInsideTileDir(workspace, tileSlug, ds)) return Results.BadRequest();
            var content = await ds.ReadOutputAsync(workspace, tileSlug);
            return content is null ? Results.NotFound() : Results.Text(content);
        }).WithTags("Dashboard");

        // Serves the raw bytes of a tile output file (used by the image template).
        api.MapGet("/projects/{slug}/dashboard/tiles/{tileSlug}/output/raw", async (string slug, string tileSlug, ProjectService ps, DashboardService ds) =>
        {
            var workspace = await ResolveDashboardWorkspaceAsync(slug, ps);
            if (workspace is null) return Results.NotFound();
            if (!IsInsideTileDir(workspace, tileSlug, ds)) return Results.BadRequest();
            var path = ds.FindOutputPath(workspace, tileSlug);
            if (path is null || !File.Exists(path)) return Results.NotFound();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var contentType = ext switch
            {
                ".png"  => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"  => "image/gif",
                ".webp" => "image/webp",
                ".svg"  => "image/svg+xml",
                _       => "application/octet-stream",
            };
            return Results.File(path, contentType);
        }).WithTags("Dashboard");

        // Overwrites the output file of a tile. Body is plain text. Template determines extension.
        api.MapPut("/projects/{slug}/dashboard/tiles/{tileSlug}/output", async (string slug, string tileSlug, HttpRequest req, ProjectService ps, DashboardService ds) =>
        {
            var workspace = await ResolveDashboardWorkspaceAsync(slug, ps);
            if (workspace is null) return Results.NotFound();
            if (!IsInsideTileDir(workspace, tileSlug, ds)) return Results.BadRequest();
            var sidecar = await ds.ReadSidecarAsync(workspace, tileSlug);
            var template = sidecar?.Template ?? TileTemplate.Markdown;
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            await ds.WriteOutputAsync(workspace, tileSlug, body, template);
            return Results.NoContent();
        }).WithTags("Dashboard");

        // Returns the parsed tile.yaml sidecar. 404 if none.
        api.MapGet("/projects/{slug}/dashboard/tiles/{tileSlug}/sidecar", async (string slug, string tileSlug, ProjectService ps, DashboardService ds) =>
        {
            var workspace = await ResolveDashboardWorkspaceAsync(slug, ps);
            if (workspace is null) return Results.NotFound();
            if (!IsInsideTileDir(workspace, tileSlug, ds)) return Results.BadRequest();
            var sidecar = await ds.ReadSidecarAsync(workspace, tileSlug);
            return sidecar is null ? Results.NotFound() : Results.Ok(sidecar);
        }).WithTags("Dashboard");

        // Creates or replaces tile.yaml.
        api.MapPut("/projects/{slug}/dashboard/tiles/{tileSlug}/sidecar", async (string slug, string tileSlug, TileSidecar sidecar, ProjectService ps, DashboardService ds) =>
        {
            var workspace = await ResolveDashboardWorkspaceAsync(slug, ps);
            if (workspace is null) return Results.NotFound();
            if (!IsInsideTileDir(workspace, tileSlug, ds)) return Results.BadRequest();
            if (string.IsNullOrWhiteSpace(sidecar.Template) || !TileTemplate.IsKnown(sidecar.Template))
                return Results.BadRequest(new { error = $"Unknown template '{sidecar.Template}'." });
            await ds.WriteSidecarAsync(workspace, tileSlug, sidecar);
            return Results.NoContent();
        }).WithTags("Dashboard");

        // Returns the script filename (script.*) if present; 404 if none.
        api.MapGet("/projects/{slug}/dashboard/tiles/{tileSlug}/script", async (string slug, string tileSlug, ProjectService ps, DashboardService ds) =>
        {
            var workspace = await ResolveDashboardWorkspaceAsync(slug, ps);
            if (workspace is null) return Results.NotFound();
            if (!IsInsideTileDir(workspace, tileSlug, ds)) return Results.BadRequest();
            var (scriptPath, error) = ds.FindScript(workspace, tileSlug);
            if (error is not null) return Results.BadRequest(new { error });
            if (scriptPath is null) return Results.NotFound();
            var content = await File.ReadAllTextAsync(scriptPath, System.Text.Encoding.UTF8);
            return Results.Text(content);
        }).WithTags("Dashboard");

        // Triggers an immediate refresh of the tile (same pipeline as auto-refresh).
        api.MapPost("/projects/{slug}/dashboard/tiles/{tileSlug}/refresh", async (string slug, string tileSlug, ProjectService ps, DashboardService ds, DashboardRefreshService refreshSvc) =>
        {
            var workspace = await ResolveDashboardWorkspaceAsync(slug, ps);
            if (workspace is null) return Results.NotFound();
            // Actual refresh is fire-and-forget; caller polls via the gate snapshot or SSE.
            _ = Task.Run(() => refreshSvc.ManualRefreshAsync(slug, workspace, tileSlug, CancellationToken.None));
            return Results.Accepted();
        }).WithTags("Dashboard");
    }

    private static async Task<string?> ResolveDashboardWorkspaceAsync(string slug, ProjectService ps)
    {
        var project = await ps.GetProjectAsync(slug);
        return project is null ? null : ps.ResolveWorkspacePath(project);
    }

    private static bool IsInsideTileDir(string workspace, string tileSlug, DashboardService ds)
    {
        var dashDir = Path.GetFullPath(ds.GetDashboardDir(workspace));
        var tileDir = Path.GetFullPath(ds.GetTileDirPath(workspace, tileSlug));
        return tileDir.StartsWith(dashDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !tileSlug.Contains(Path.DirectorySeparatorChar)
            && !tileSlug.Contains(Path.AltDirectorySeparatorChar);
    }
}
