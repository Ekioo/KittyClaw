using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KittyClaw.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

// X, Y, Width, Height are pixel values snapped to the 20px grid.
// Slug is the tile folder name inside .dashboard/ -- the single identity of the tile.
public record DashboardTileLayout(string Slug, int X = 0, int Y = 0, int Width = 300, int Height = 200);

public sealed class DashboardTilePathException() : IOException(InvalidTilePathMessage)
{
    public const string InvalidTilePathMessage = "Dashboard files must stay in an ordinary tile directory.";
}

public class DashboardService
{
    private readonly ProjectService _projectService;
    private readonly ILogger<DashboardService> _logger;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    // Convention-based filenames inside each tile folder.
    public const string SidecarFileName = "tile.yaml";
    public const string ScriptBaseName  = "script";
    public const string OutputBaseName  = "output";

    private static readonly HashSet<string> ScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".sh", ".js", ".py" };

    public DashboardService(ProjectService projectService, ILogger<DashboardService>? logger = null)
    {
        _projectService = projectService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DashboardService>.Instance;
    }

    // -- Layout (per-project SQLite) ---------------------------------------------

    private Task EnsureTableAsync(string slug)
    {
        var dbPath = _projectService.GetProjectDbPath(slug);
        return MigrationGate.RunOnceAsync(dbPath, "dashboard-layout", async () =>
        {
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
        });
    }

    private async Task<List<DashboardTileLayout>> GetStoredTilesAsync(string slug)
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize LayoutJson for project {Slug}; returning empty layout", slug);
            return [];
        }
    }

    // Disk-driven: a tile is visible as soon as its .dashboard/<slug>/ folder exists, no
    // registration required. Layout comes from the stored SQLite row if present, else a
    // deterministic grid default computed from the slug's position in the folder listing.
    public async Task<List<DashboardTileLayout>> GetTilesAsync(string projectSlug, string workspace)
    {
        var slugs = GetAvailableSlugs(workspace);
        var stored = (await GetStoredTilesAsync(projectSlug)).ToDictionary(t => t.Slug);
        return slugs
            .Select((slug, i) => stored.TryGetValue(slug, out var t) ? t : DefaultLayout(slug, i))
            .ToList();
    }

    private static DashboardTileLayout DefaultLayout(string slug, int index)
    {
        var col = index % 4;
        var row = index / 4;
        return new DashboardTileLayout(slug, X: col * 320, Y: row * 220);
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

    public async Task<bool> RemoveTileAsync(string projectSlug, string tileSlug)
    {
        var tiles = await GetStoredTilesAsync(projectSlug);
        var removed = tiles.RemoveAll(t => t.Slug == tileSlug) > 0;
        if (removed) await SaveTilesAsync(projectSlug, tiles);
        return removed;
    }

    // Seeds a DB row from the grid default on first move/resize -- no separate "register" step.
    private async Task<DashboardTileLayout?> UpsertTileAsync(
        string projectSlug, string workspace, string tileSlug, Func<DashboardTileLayout, DashboardTileLayout> apply)
    {
        if (!IsTileDirectoryAllowed(workspace, tileSlug) ||
            !Directory.Exists(GetTileDirPath(workspace, tileSlug))) return null;

        var tiles = await GetStoredTilesAsync(projectSlug);
        var idx = tiles.FindIndex(t => t.Slug == tileSlug);
        var current = idx >= 0
            ? tiles[idx]
            : DefaultLayout(tileSlug, GetAvailableSlugs(workspace).IndexOf(tileSlug));

        var updated = apply(current);
        if (idx >= 0) tiles[idx] = updated; else tiles.Add(updated);
        await SaveTilesAsync(projectSlug, tiles);
        return updated;
    }

    public Task<DashboardTileLayout?> MoveTileAsync(string projectSlug, string workspace, string tileSlug, int x, int y) =>
        UpsertTileAsync(projectSlug, workspace, tileSlug, t => t with { X = Snap(Math.Max(0, x)), Y = Snap(Math.Max(0, y)) });

    public Task<DashboardTileLayout?> ResizeTileAsync(string projectSlug, string workspace, string tileSlug, int width, int height) =>
        UpsertTileAsync(projectSlug, workspace, tileSlug, t => t with { Width = Snap(Math.Max(100, width)), Height = Snap(Math.Max(60, height)) });

    private static int Snap(int v) => (int)Math.Round((double)v / 20) * 20;

    // -- Tile refresh state (per-project SQLite) ---------------------------------

    private Task EnsureRefreshStateTableAsync(string projectSlug)
    {
        var dbPath = _projectService.GetProjectDbPath(projectSlug);
        return MigrationGate.RunOnceAsync(dbPath, "dashboard-refresh-state", async () =>
        {
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
        });
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

    public bool IsTileDirectoryAllowed(string workspace, string tileSlug) =>
        IsValidTileSlug(tileSlug) && !IsReparsePoint(GetDashboardDir(workspace))
        && !IsReparsePoint(GetTileDirPath(workspace, tileSlug));

    private static bool IsValidTileSlug(string slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug is not ("." or "..")
        && slug.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && (!OperatingSystem.IsWindows() || (!slug.EndsWith('.') && !slug.EndsWith(' ')));

    public string? FindOutputPath(string workspace, string tileSlug)
    {
        if (!IsTileDirectoryAllowed(workspace, tileSlug)) return null;
        var dir = GetTileDirPath(workspace, tileSlug);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, OutputBaseName + ".*")
            .FirstOrDefault(path => !IsReparsePoint(path));
    }

    public (string? Path, string? ConfigError) FindScript(string workspace, string tileSlug)
    {
        if (!IsTileDirectoryAllowed(workspace, tileSlug)) return (null, null);
        var dir = GetTileDirPath(workspace, tileSlug);
        if (!Directory.Exists(dir)) return (null, null);

        var scripts = Directory.GetFiles(dir, ScriptBaseName + ".*", SearchOption.TopDirectoryOnly)
            .Where(f => ScriptExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (scripts.Any(IsReparsePoint))
            return (null, "Dashboard scripts must be regular files.");

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
        if (!Directory.Exists(dir) || IsReparsePoint(dir)) return [];
        return new DirectoryInfo(dir).EnumerateDirectories()
            .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(directory => directory.Name)
            .Where(IsValidTileSlug)
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
        var path = PrepareTileFileForWrite(workspace, tileSlug, OutputBaseName + TileTemplate.DefaultExtension(template));
        var previousOutputs = Directory.GetFiles(Path.GetDirectoryName(path)!, OutputBaseName + ".*");
        if (previousOutputs.Any(IsReparsePoint)) throw new DashboardTilePathException();
        foreach (var old in previousOutputs)
            File.Delete(old);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
    }

    public void DeleteTileFolder(string workspace, string tileSlug)
    {
        if (!IsTileDirectoryAllowed(workspace, tileSlug)) throw new DashboardTilePathException();
        var dir = GetTileDirPath(workspace, tileSlug);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    // -- Sidecar -----------------------------------------------------------------

    public async Task<TileSidecar?> ReadSidecarAsync(string workspace, string tileSlug)
    {
        if (!IsTileDirectoryAllowed(workspace, tileSlug)) return null;
        var path = GetSidecarPath(workspace, tileSlug);
        if (!File.Exists(path) || IsReparsePoint(path)) return null;
        var yaml = await File.ReadAllTextAsync(path, Encoding.UTF8);
        return TileSidecarSerializer.TryParse(yaml);
    }

    public async Task WriteSidecarAsync(string workspace, string tileSlug, TileSidecar sidecar)
    {
        var path = PrepareTileFileForWrite(workspace, tileSlug, SidecarFileName);
        var yaml = TileSidecarSerializer.Serialize(sidecar);
        await File.WriteAllTextAsync(path, yaml, Encoding.UTF8);
    }

    public async Task<string> WriteScriptAsync(string workspace, string tileSlug, string content)
    {
        var path = PrepareTileFileForWrite(workspace, tileSlug, ScriptBaseName + DefaultScriptExtension());
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        return path;
    }

    private string PrepareTileFileForWrite(string workspace, string tileSlug, string filename)
    {
        if (!IsTileDirectoryAllowed(workspace, tileSlug)) throw new DashboardTilePathException();
        var directory = GetTileDirPath(workspace, tileSlug);
        var path = Path.Combine(directory, filename);
        if (IsReparsePoint(path)) throw new DashboardTilePathException();
        Directory.CreateDirectory(directory);
        return path;
    }

    // -- Startup migration (flat to folder-per-tile) --------------------------------

    public virtual async Task MigrateAsync(string projectSlug, string workspace, Action<string>? log = null)
    {
        var dashDir = Path.GetFullPath(GetDashboardDir(workspace));
        if (!Directory.Exists(dashDir) || IsReparsePoint(dashDir)) return;

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
                if (!IsTileDirectoryAllowed(workspace, tileSlug)) continue;

                var tileDir = GetTileDirPath(workspace, tileSlug);
                if (Directory.Exists(tileDir))
                {
                    log?.Invoke($"[migrate] {tileSlug}: folder already exists, skipping");
                    continue;
                }

                if (IsReparsePoint(sidecarPath))
                    throw new InvalidDataException("Legacy tile metadata must be a regular dashboard file.");

                var oldYaml = await File.ReadAllTextAsync(sidecarPath, Encoding.UTF8);
                var oldSidecar = TileSidecarSerializer.TryParse(oldYaml);
                var legacyScript = ExtractLegacyScriptField(oldYaml);

                var scriptSrc = legacyScript is null ? null : ResolveLegacyScript(dashDir, legacyScript);
                if (scriptSrc is null)
                {
                    foreach (var ext in ScriptExtensions)
                    {
                        scriptSrc = ResolveLegacyScript(dashDir, tileSlug + ext);
                        if (scriptSrc is not null) break;
                    }
                }

                var resultSrc = Path.Combine(dashDir, resultFile);
                if (IsReparsePoint(resultSrc))
                    throw new InvalidDataException("Legacy tile output must be a regular dashboard file.");

                Directory.CreateDirectory(tileDir);
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
                _logger.LogWarning(ex, "Failed to migrate tile {Sidecar} for project {Slug}", Path.GetFileName(sidecarPath), projectSlug);
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LayoutJson for project {Slug} is not in current format; attempting legacy migration", projectSlug);
        }

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy layout for project {Slug}; layout will remain as-is", projectSlug);
        }
    }

    private record LegacyTileLayout(string FileName = "", int X = 0, int Y = 0, int Width = 300, int Height = 200);

    private static string? ResolveLegacyScript(string dashboard, string script)
    {
        const string error = "Legacy tile script must be a supported file inside the dashboard directory.";
        if (Path.IsPathRooted(script))
            throw new InvalidDataException(error);

        var relative = Path.GetRelativePath(dashboard, Path.GetFullPath(script, dashboard));
        if (Path.IsPathRooted(relative) || relative is "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !ScriptExtensions.Contains(Path.GetExtension(relative)))
            throw new InvalidDataException(error);

        var path = dashboard;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException(error);
            path = Path.Combine(path, segment);
            if (IsReparsePoint(path))
                throw new InvalidDataException(error);
        }

        return File.Exists(path) ? path : null;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

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
