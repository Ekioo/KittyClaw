using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Services;

// X, Y, Width, Height are pixel values snapped to the 20px grid
public record DashboardTileLayout(string FileName, int X = 0, int Y = 0, int Width = 300, int Height = 200);

public class DashboardService
{
    private readonly ProjectService _projectService;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public const string SidecarSuffix = ".yaml";

    public DashboardService(ProjectService projectService)
    {
        _projectService = projectService;
    }

    // ── Layout (per-project SQLite) ──────────────────────────────────────────

    private async Task EnsureTableAsync(string slug)
    {
        var dbPath = _projectService.GetProjectDbPath(slug);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS DashboardLayout (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                LayoutJson TEXT NOT NULL DEFAULT '[]'
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<DashboardTileLayout>> GetTilesAsync(string slug)
    {
        await EnsureTableAsync(slug);
        var dbPath = _projectService.GetProjectDbPath(slug);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LayoutJson FROM DashboardLayout WHERE Id = 1";
        var result = await cmd.ExecuteScalarAsync();
        if (result is null) return [];
        try { return JsonSerializer.Deserialize<List<DashboardTileLayout>>(result.ToString()!, _json) ?? []; }
        catch { return []; }
    }

    private async Task SaveTilesAsync(string slug, List<DashboardTileLayout> tiles)
    {
        await EnsureTableAsync(slug);
        var dbPath = _projectService.GetProjectDbPath(slug);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DashboardLayout (Id, LayoutJson) VALUES (1, $json)
            ON CONFLICT(Id) DO UPDATE SET LayoutJson = $json
            """;
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(tiles));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DashboardTileLayout?> AddTileAsync(string slug, string fileName)
    {
        var tiles = await GetTilesAsync(slug);
        if (tiles.Any(t => t.FileName == fileName)) return tiles.First(t => t.FileName == fileName);

        var idx = tiles.Count;
        var col = idx % 4;
        var row = idx / 4;
        var tile = new DashboardTileLayout(fileName, X: col * 320, Y: row * 220);
        tiles.Add(tile);
        await SaveTilesAsync(slug, tiles);
        return tile;
    }

    public async Task<bool> RemoveTileAsync(string slug, string fileName)
    {
        var tiles = await GetTilesAsync(slug);
        var removed = tiles.RemoveAll(t => t.FileName == fileName) > 0;
        if (removed) await SaveTilesAsync(slug, tiles);
        return removed;
    }

    public async Task<DashboardTileLayout?> MoveTileAsync(string slug, string fileName, int x, int y)
    {
        var tiles = await GetTilesAsync(slug);
        var idx = tiles.FindIndex(t => t.FileName == fileName);
        if (idx < 0) return null;
        x = Snap(Math.Max(0, x));
        y = Snap(Math.Max(0, y));
        var updated = tiles[idx] with { X = x, Y = y };
        tiles[idx] = updated;
        await SaveTilesAsync(slug, tiles);
        return updated;
    }

    public async Task<DashboardTileLayout?> ResizeTileAsync(string slug, string fileName, int width, int height)
    {
        var tiles = await GetTilesAsync(slug);
        var idx = tiles.FindIndex(t => t.FileName == fileName);
        if (idx < 0) return null;
        width = Snap(Math.Max(100, width));
        height = Snap(Math.Max(60, height));
        var updated = tiles[idx] with { Width = width, Height = height };
        tiles[idx] = updated;
        await SaveTilesAsync(slug, tiles);
        return updated;
    }

    private static int Snap(int v) => (int)Math.Round((double)v / 20) * 20;

    // ── Files (.dashboard/) ─────────────────────────────────────────────────

    public string GetDashboardDir(string workspace) => Path.Combine(workspace, ".dashboard");
    public string GetFilePath(string workspace, string fileName) => Path.Combine(GetDashboardDir(workspace), fileName);
    public string GetSidecarPath(string workspace, string fileName) => GetFilePath(workspace, fileName) + SidecarSuffix;

    /// <summary>Lists tile result files, excluding the .yaml sidecars.</summary>
    public List<string> GetAvailableFiles(string workspace)
    {
        var dir = GetDashboardDir(workspace);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(f => f is not null && !f.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .OrderBy(f => f)
            .ToList();
    }

    public async Task<string?> ReadFileContentAsync(string workspace, string fileName)
    {
        var path = GetFilePath(workspace, fileName);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, Encoding.UTF8);
    }

    public async Task WriteFileAsync(string workspace, string fileName, string content)
    {
        var dir = GetDashboardDir(workspace);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(GetFilePath(workspace, fileName), content, Encoding.UTF8);
    }

    public void DeleteTileFiles(string workspace, string fileName)
    {
        var path = GetFilePath(workspace, fileName);
        if (File.Exists(path)) File.Delete(path);
        var side = GetSidecarPath(workspace, fileName);
        if (File.Exists(side)) File.Delete(side);
    }

    // ── Sidecar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>{fileName}.yaml</c> if present. Also performs a one-shot migration of legacy
    /// in-file YAML front-matter (--- refresh: ... ---) into a sidecar so older tiles keep working.
    /// </summary>
    public async Task<TileSidecar?> ReadSidecarAsync(string workspace, string fileName)
    {
        await TryMigrateLegacyHeaderAsync(workspace, fileName);

        var path = GetSidecarPath(workspace, fileName);
        if (!File.Exists(path)) return null;
        var yaml = await File.ReadAllTextAsync(path, Encoding.UTF8);
        return TileSidecarSerializer.TryParse(yaml);
    }

    public async Task WriteSidecarAsync(string workspace, string fileName, TileSidecar sidecar)
    {
        var dir = GetDashboardDir(workspace);
        Directory.CreateDirectory(dir);
        var yaml = TileSidecarSerializer.Serialize(sidecar);
        await File.WriteAllTextAsync(GetSidecarPath(workspace, fileName), yaml, Encoding.UTF8);
    }

    /// <summary>
    /// Detects the legacy front-matter format and converts it to a sidecar in-place.
    /// Idempotent: does nothing if there's already a sidecar or no legacy header.
    /// </summary>
    private async Task TryMigrateLegacyHeaderAsync(string workspace, string fileName)
    {
        var filePath = GetFilePath(workspace, fileName);
        var sidePath = GetSidecarPath(workspace, fileName);
        if (!File.Exists(filePath) || File.Exists(sidePath)) return;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext != ".md" && ext != ".json") return; // legacy headers only ever lived in md/json

        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        var legacy = ParseLegacyHeader(content);
        if (legacy is null) return;

        var body = ExtractLegacyBody(content);
        var template = ext == ".md" ? TileTemplate.Markdown : TileTemplate.Table;
        var sidecar = new TileSidecar(template, legacy.RefreshSeconds, legacy.Prompt, legacy.Model);

        await File.WriteAllTextAsync(sidePath, TileSidecarSerializer.Serialize(sidecar), Encoding.UTF8);
        await File.WriteAllTextAsync(filePath, body, Encoding.UTF8);
    }

    private record LegacyHeader(int RefreshSeconds, string Prompt, string? Model);

    private static LegacyHeader? ParseLegacyHeader(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return null;
        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0) return null;

        int refresh = 0;
        string? prompt = null;
        string? model = null;
        for (int i = 1; i < end; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon < 0) continue;
            var key = lines[i][..colon].Trim().ToLowerInvariant();
            var val = lines[i][(colon + 1)..].Trim();
            switch (key)
            {
                case "refresh": int.TryParse(val, out refresh); break;
                case "prompt": prompt = val; break;
                case "model": model = string.IsNullOrWhiteSpace(val) ? null : val; break;
            }
        }
        if (refresh <= 0 || string.IsNullOrWhiteSpace(prompt)) return null;
        return new LegacyHeader(refresh, prompt!, model);
    }

    private static string ExtractLegacyBody(string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return content;
        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0) return content;
        return string.Join('\n', lines.Skip(end + 1)).TrimStart('\n');
    }
}
