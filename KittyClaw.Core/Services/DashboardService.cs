using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Services;

// X, Y, Width, Height are pixel values snapped to the 20px grid.
// Slug is the tile folder name inside .dashboard/ -- the single identity of the tile.
public record DashboardTileLayout(string Slug, int X = 0, int Y = 0, int Width = 300, int Height = 200);

public class DashboardService
{
    private readonly ProjectService _projectService;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    // Convention-based filenames inside each tile folder.
    public const string SidecarFileName = "tile.yaml";
    public const string ScriptBaseName  = "script";
    public const string OutputBaseName  = "output";

    private static readonly HashSet<string> ScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".sh", ".js", ".py" };

    public DashboardService(ProjectService projectService)
    {
        _projectService = projectService;
    }

    // -- Layout (per-project SQLite) ---------------------------------------------

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

    public async Task<DashboardTileLayout?> AddTileAsync(string projectSlug, string tileSlug)
    {
        var tiles = await GetTilesAsync(projectSlug);
        if (tiles.Any(t => t.Slug == tileSlug)) return tiles.First(t => t.Slug == tileSlug);

        var idx = tiles.Count;
        var col = idx % 4;
        var row = idx / 4;
        var tile = new DashboardTileLayout(tileSlug, X: col * 320, Y: row * 220);
        tiles.Add(tile);
        await SaveTilesAsync(projectSlug, tiles);
        return tile;
    }

    public async Task<bool> RemoveTileAsync(string projectSlug, string tileSlug)
    {
        var tiles = await GetTilesAsync(projectSlug);
        var removed = tiles.RemoveAll(t => t.Slug == tileSlug) > 0;
        if (removed) await SaveTilesAsync(projectSlug, tiles);
        return removed;
    }

    public async Task<DashboardTileLayout?> MoveTileAsync(string projectSlug, string tileSlug, int x, int y)
    {
        var tiles = await GetTilesAsync(projectSlug);
        var idx = tiles.FindIndex(t => t.Slug == tileSlug);
        if (idx < 0) return null;
        x = Snap(Math.Max(0, x));
        y = Snap(Math.Max(0, y));
        var updated = tiles[idx] with { X = x, Y = y };
        tiles[idx] = updated;
        await SaveTilesAsync(projectSlug, tiles);
        return updated;
    }

    public async Task<DashboardTileLayout?> ResizeTileAsync(string projectSlug, string tileSlug, int width, int height)
    {
        var tiles = await GetTilesAsync(projectSlug);
        var idx = tiles.FindIndex(t => t.Slug == tileSlug);
        if (idx < 0) return null;
        width = Snap(Math.Max(100, width));
        height = Snap(Math.Max(60, height));
        var updated = tiles[idx] with { Width = width, Height = height };
        tiles[idx] = updated;
        await SaveTilesAsync(projectSlug, tiles);
        return updated;
    }

    private static int Snap(int v) => (int)Math.Round((double)v / 20) * 20;

    // -- Tile refresh state (per-project SQLite) ---------------------------------

    private async Task EnsureRefreshStateTableAsync(string projectSlug)
    {
        var dbPath = _projectService.GetProjectDbPath(projectSlug);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS dashboard_tile_refresh_state (
                TileKey TEXT NOT NULL PRIMARY KEY,
                LastRefreshedAt TEXT NOT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DateTime?> GetLastRefreshedAtAsync(string projectSlug, string tileSlug)
    {
        await EnsureRefreshStateTableAsync(projectSlug);
        var dbPath = _projectService.GetProjectDbPath(projectSlug);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LastRefreshedAt FROM dashboard_tile_refresh_state WHERE TileKey = $key";
        cmd.Parameters.AddWithValue("$key", $"{projectSlug}:{tileSlug}");
        var raw = await cmd.ExecuteScalarAsync() as string;
        if (raw is null) return null;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }

    public async Task SetLastRefreshedAtAsync(string projectSlug, string tileSlug, DateTime lastRefreshedAt)
    {
        await EnsureRefreshStateTableAsync(projectSlug);
        var dbPath = _projectService.GetProjectDbPath(projectSlug);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dashboard_tile_refresh_state (TileKey, LastRefreshedAt)
            VALUES ($key, $ts)
            ON CONFLICT(TileKey) DO UPDATE SET LastRefreshedAt = $ts
            """;
        cmd.Parameters.AddWithValue("$key", $"{projectSlug}:{tileSlug}");
        cmd.Parameters.AddWithValue("$ts", lastRefreshedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    // -- File paths (.dashboard/<slug>/) -----------------------------------------

    public string GetDashboardDir(string workspace) => Path.Combine(workspace, ".dashboard");

    public string GetTileDirPath(string workspace, string tileSlug) =>
        Path.Combine(GetDashboardDir(workspace), tileSlug);

    public string GetSidecarPath(string workspace, string tileSlug) =>
        Path.Combine(GetTileDirPath(workspace, tileSlug), SidecarFileName);

    public string GetOutputPath(string workspace, string tileSlug, string template) =>
        Path.Combine(GetTileDirPath(workspace, tileSlug), OutputBaseName + TileTemplate.DefaultExtension(template));

    public string? FindOutputPath(string workspace, string tileSlug)
    {
        var dir = GetTileDirPath(workspace, tileSlug);
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, OutputBaseName + ".*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
    }

    public (string? Path, string? ConfigError) FindScript(string workspace, string tileSlug)
    {
        var dir = GetTileDirPath(workspace, tileSlug);
        if (!Directory.Exists(dir)) return (null, null);

        var scripts = Directory.GetFiles(dir, ScriptBaseName + ".*", SearchOption.TopDirectoryOnly)
            .Where(f => ScriptExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        return scripts.Count switch
        {
            0 => (null, null),
            1 => (scripts[0], null),
            _ => (null, $"Multiple scripts found in {dir}; keep exactly one script.* file."),
        };
    }

    public List<string> GetAvailableSlugs(string workspace)
    {
        var dir = GetDashboardDir(workspace);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
    }

    // -- File I/O ----------------------------------------------------------------

    public async Task<string?> ReadOutputAsync(string workspace, string tileSlug)
    {
        var path = FindOutputPath(workspace, tileSlug);
        if (path is null || !File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, Encoding.UTF8);
    }

    public async Task WriteOutputAsync(string workspace, string tileSlug, string content, string template)
    {
        var dir = GetTileDirPath(workspace, tileSlug);
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, OutputBaseName + ".*", SearchOption.TopDirectoryOnly))
            File.Delete(old);
        var path = GetOutputPath(workspace, tileSlug, template);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
    }

    public void DeleteTileFolder(string workspace, string tileSlug)
    {
        var dir = GetTileDirPath(workspace, tileSlug);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    // -- Sidecar -----------------------------------------------------------------

    public async Task<TileSidecar?> ReadSidecarAsync(string workspace, string tileSlug)
    {
        var path = GetSidecarPath(workspace, tileSlug);
        if (!File.Exists(path)) return null;
        var yaml = await File.ReadAllTextAsync(path, Encoding.UTF8);
        return TileSidecarSerializer.TryParse(yaml);
    }

    public async Task WriteSidecarAsync(string workspace, string tileSlug, TileSidecar sidecar)
    {
        var dir = GetTileDirPath(workspace, tileSlug);
        Directory.CreateDirectory(dir);
        var yaml = TileSidecarSerializer.Serialize(sidecar);
        await File.WriteAllTextAsync(GetSidecarPath(workspace, tileSlug), yaml, Encoding.UTF8);
    }

    // -- Startup migration (flat to folder-per-tile) --------------------------------

    public async Task MigrateAsync(string projectSlug, string workspace, Action<string>? log = null)
    {
        var dashDir = GetDashboardDir(workspace);
        if (!Directory.Exists(dashDir)) return;

        var flatSidecars = Directory.GetFiles(dashDir, "*.yaml", SearchOption.TopDirectoryOnly)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                var withoutYaml = name[..^".yaml".Length];
                return Path.GetExtension(withoutYaml).Length > 0;
            })
            .ToList();

        foreach (var sidecarPath in flatSidecars)
        {
            try
            {
                var sidecarFile = Path.GetFileName(sidecarPath);
                var resultFile  = sidecarFile[..^".yaml".Length];
                var tileSlug    = Path.GetFileNameWithoutExtension(resultFile);
                if (string.IsNullOrWhiteSpace(tileSlug)) continue;

                var tileDir = GetTileDirPath(workspace, tileSlug);
                if (Directory.Exists(tileDir))
                {
                    log?.Invoke($"[migrate] {tileSlug}: folder already exists, skipping");
                    continue;
                }

                Directory.CreateDirectory(tileDir);

                var oldYaml       = await File.ReadAllTextAsync(sidecarPath, Encoding.UTF8);
                var oldSidecar    = TileSidecarSerializer.TryParse(oldYaml);
                var legacyScript  = ExtractLegacyScriptField(oldYaml);

                string? scriptSrc = null;
                if (legacyScript is not null)
                {
                    var c = Path.Combine(dashDir, legacyScript);
                    if (File.Exists(c)) scriptSrc = c;
                }
                if (scriptSrc is null)
                {
                    foreach (var ext in ScriptExtensions)
                    {
                        var c = Path.Combine(dashDir, tileSlug + ext);
                        if (File.Exists(c)) { scriptSrc = c; break; }
                    }
                }

                var resultSrc = Path.Combine(dashDir, resultFile);
                if (File.Exists(resultSrc))
                {
                    var outputExt = Path.GetExtension(resultFile);
                    var outputDst = Path.Combine(tileDir, OutputBaseName + outputExt);
                    File.Move(resultSrc, outputDst);
                    log?.Invoke($"[migrate] {tileSlug}: {resultFile} -> output{outputExt}");
                }

                if (scriptSrc is not null && File.Exists(scriptSrc))
                {
                    var scriptExt = Path.GetExtension(scriptSrc);
                    var scriptDst = Path.Combine(tileDir, ScriptBaseName + scriptExt);
                    if (!File.Exists(scriptDst))
                    {
                        File.Move(scriptSrc, scriptDst);
                        log?.Invoke($"[migrate] {tileSlug}: {Path.GetFileName(scriptSrc)} -> script{scriptExt}");
                    }
                }

                var newYaml = oldSidecar is not null
                    ? TileSidecarSerializer.Serialize(oldSidecar)
                    : oldYaml;
                await File.WriteAllTextAsync(GetSidecarPath(workspace, tileSlug), newYaml, Encoding.UTF8);

                File.Delete(sidecarPath);
                log?.Invoke($"[migrate] {tileSlug}: done");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[migrate] WARNING: {Path.GetFileName(sidecarPath)}: {ex.Message}");
            }
        }

        await MigrateLayoutDbAsync(projectSlug);
    }

    private async Task MigrateLayoutDbAsync(string projectSlug)
    {
        if (_projectService is null) return; // skip in unit-test context without DB
        await EnsureTableAsync(projectSlug);
        var dbPath = _projectService.GetProjectDbPath(projectSlug);
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LayoutJson FROM DashboardLayout WHERE Id = 1";
        var raw = await cmd.ExecuteScalarAsync() as string;
        if (string.IsNullOrWhiteSpace(raw) || raw == "[]") return;

        try
        {
            var current = JsonSerializer.Deserialize<List<DashboardTileLayout>>(raw, _json) ?? [];
            if (current.Any(t => !string.IsNullOrWhiteSpace(t.Slug))) return;
        }
        catch { }

        try
        {
            var legacy = JsonSerializer.Deserialize<List<LegacyTileLayout>>(raw, _json) ?? [];
            if (legacy.Count == 0) return;
            var migrated = legacy
                .Select(e => new DashboardTileLayout(
                    Slug: Path.GetFileNameWithoutExtension(e.FileName),
                    X: e.X, Y: e.Y, Width: e.Width, Height: e.Height))
                .Where(e => !string.IsNullOrWhiteSpace(e.Slug))
                .DistinctBy(e => e.Slug)
                .ToList();
            await SaveTilesAsync(projectSlug, migrated);
        }
        catch { }
    }

    private record LegacyTileLayout(string FileName = "", int X = 0, int Y = 0, int Width = 300, int Height = 200);

    private static string? ExtractLegacyScriptField(string yaml)
    {
        foreach (var line in yaml.ReplaceLineEndings("\n").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            if (!key.Equals("script", StringComparison.OrdinalIgnoreCase)) continue;
            var val = line[(colon + 1)..].Trim().Trim('"', '\'');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }

    public static string DefaultScriptExtension() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".ps1" : ".sh";
}