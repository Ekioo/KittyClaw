using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Services;

// X, Y, Width, Height are pixel values snapped to the 20px grid
public record DashboardTileLayout(string FileName, int X = 0, int Y = 0, int Width = 300, int Height = 200);

/// <summary>
/// Parsed front-matter header from a dashboard file.
/// Files without this header are static and never auto-refreshed.
/// </summary>
/// <param name="RefreshSeconds">How often to re-run the prompt, in seconds.</param>
/// <param name="Prompt">The LLM instruction to execute on each refresh.</param>
/// <param name="Model">Optional Claude model override (null = project default).</param>
public record DashboardFileHeader(int RefreshSeconds, string Prompt, string? Model);

public class DashboardService
{
    private readonly ProjectService _projectService;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public DashboardService(ProjectService projectService)
    {
        _projectService = projectService;
    }

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
        catch { return []; } // silently discard data from old schema
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

        // Auto-place in a 4-column staggered layout so tiles don't all overlap
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

    public List<string> GetAvailableFiles(string workspace)
    {
        var dashDir = Path.Combine(workspace, ".dashboard");
        if (!Directory.Exists(dashDir)) return [];
        return Directory.GetFiles(dashDir, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Cast<string>()
            .OrderBy(f => f)
            .ToList();
    }

    public async Task<string?> ReadFileContentAsync(string workspace, string fileName)
    {
        var path = Path.Combine(workspace, ".dashboard", fileName);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
    }

    public async Task WriteFileAsync(string workspace, string fileName, string content)
    {
        var dashDir = Path.Combine(workspace, ".dashboard");
        Directory.CreateDirectory(dashDir);
        await File.WriteAllTextAsync(Path.Combine(dashDir, fileName), content, System.Text.Encoding.UTF8);
    }

    private static int Snap(int v) => (int)Math.Round((double)v / 20) * 20;

    // --- Header parsing ---

    private const string HeaderDelimiter = "---";

    /// <summary>
    /// Parses the YAML-like front-matter header from a dashboard file.
    /// Returns null if the file has no header or the header is malformed.
    /// Expected format:
    /// ---
    /// refresh: 3600
    /// prompt: Your LLM instruction here
    /// model: claude-haiku-4-5-20251001
    /// ---
    /// </summary>
    public static DashboardFileHeader? ParseHeader(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != HeaderDelimiter) return null;

        var end = Array.FindIndex(lines, 1, l => l.Trim() == HeaderDelimiter);
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
        return new DashboardFileHeader(refresh, prompt!, model);
    }

    /// <summary>Returns the body of a dashboard file, stripping the front-matter header if present.</summary>
    public static string ExtractBody(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != HeaderDelimiter) return content;
        var end = Array.FindIndex(lines, 1, l => l.Trim() == HeaderDelimiter);
        if (end < 0) return content;
        return string.Join('\n', lines.Skip(end + 1)).TrimStart('\n');
    }

    /// <summary>Serialises a header + body back to the dashboard file format.</summary>
    public static string BuildContent(DashboardFileHeader header, string body)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(HeaderDelimiter);
        sb.AppendLine($"refresh: {header.RefreshSeconds}");
        sb.AppendLine($"prompt: {header.Prompt}");
        if (header.Model is not null) sb.AppendLine($"model: {header.Model}");
        sb.AppendLine(HeaderDelimiter);
        sb.Append(body);
        return sb.ToString();
    }
}
