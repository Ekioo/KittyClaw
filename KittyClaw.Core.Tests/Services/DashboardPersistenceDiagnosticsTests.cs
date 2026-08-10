using KittyClaw.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Proves that corrupt or inaccessible dashboard persistence data:
///   1. Still falls back gracefully (user-facing behavior preserved).
///   2. Emits a Warning-level log so failures are observable.
/// </summary>
public sealed class DashboardPersistenceDiagnosticsTests
{
    // ── Shared test double ───────────────────────────────────────────────────

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((level, formatter(state, exception), exception));
        }

        public bool HasWarning(string fragment) =>
            Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    // ── DashboardTileGate ────────────────────────────────────────────────────

    /// <summary>
    /// When the registry DB file is corrupt, LoadLastFinishedAt must fall back to an empty
    /// dictionary (tile still runs) and emit a Warning log.
    /// </summary>
    [Fact]
    public async Task TileGate_CorruptRegistryDb_RunsWorkAndLogsWarning()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kc-test-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Write a non-SQLite file at the registry path so Open() throws.
            var registryPath = Path.Combine(tmp, "registry.db");
            await File.WriteAllTextAsync(registryPath, "not a sqlite database !!!");

            var ps = new ProjectService(tmp);
            var logger = new CapturingLogger<DashboardTileGate>();
            using var gate = new DashboardTileGate(ps, logger);

            var workRan = false;
            await gate.RunAsync("proj", "tile-a", manual: false, async ct =>
            {
                workRan = true;
                await Task.CompletedTask;
            }, CancellationToken.None);

            Assert.True(workRan, "Work should still execute despite corrupt DB");
            Assert.True(logger.HasWarning("LastFinishedAt"),
                "Expected a Warning about LastFinishedAt; got: " + string.Join("; ", logger.Entries.Select(e => e.Message)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// When the registry DB is corrupt, PersistLastFinishedAt must not throw and must log a Warning.
    /// </summary>
    [Fact]
    public async Task TileGate_CorruptRegistryDb_PersistLogsWarning()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kc-test-gate-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // Corrupt registry so both load and persist fail.
            await File.WriteAllTextAsync(Path.Combine(tmp, "registry.db"), "CORRUPT");

            var ps = new ProjectService(tmp);
            var logger = new CapturingLogger<DashboardTileGate>();
            using var gate = new DashboardTileGate(ps, logger);

            // RunAsync calls PersistLastFinishedAtAsync in its finally block.
            await gate.RunAsync("proj", "tile-b", manual: false, _ => Task.CompletedTask, CancellationToken.None);

            // At least one Warning must mention the persist step.
            var persistWarnings = logger.Entries
                .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("tile-b", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.NotEmpty(persistWarnings);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ── DashboardService.GetStoredTilesAsync ─────────────────────────────────

    /// <summary>
    /// When LayoutJson in the DB contains corrupt JSON, GetTilesAsync must return the
    /// disk-driven default layout and emit a Warning log.
    /// </summary>
    [Fact]
    public async Task DashboardService_CorruptLayoutJson_ReturnsFallbackAndLogsWarning()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kc-test-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var ps = new ProjectService(tmp);
            var logger = new CapturingLogger<DashboardService>();
            var svc = new DashboardService(ps, logger);

            // Write corrupt JSON into the layout DB row directly.
            var dbPath = ps.GetProjectDbPath("test-proj");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS DashboardLayout (
                        Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                        LayoutJson TEXT NOT NULL DEFAULT '[]'
                    )
                    """;
                await create.ExecuteNonQueryAsync();
                await using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO DashboardLayout (Id, LayoutJson) VALUES (1, $j)";
                insert.Parameters.AddWithValue("$j", "{ this is not valid json [[[");
                await insert.ExecuteNonQueryAsync();
            }

            // Create a tile folder so GetTilesAsync has something to return.
            var tileDir = Path.Combine(tmp, ".dashboard", "my-tile");
            Directory.CreateDirectory(tileDir);

            var tiles = await svc.GetTilesAsync("test-proj", tmp);

            // Fallback: tile still appears with default layout.
            Assert.Single(tiles);
            Assert.Equal("my-tile", tiles[0].Slug);

            // Warning must have been logged.
            Assert.True(logger.HasWarning("LayoutJson"),
                "Expected Warning about LayoutJson; got: " + string.Join("; ", logger.Entries.Select(e => e.Message)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ── DashboardService.MigrateLayoutDbAsync ────────────────────────────────

    /// <summary>
    /// When MigrateLayoutDbAsync encounters JSON it cannot parse as either current or legacy
    /// format, it must complete without throwing and emit a Warning log.
    /// </summary>
    [Fact]
    public async Task DashboardService_CorruptLayoutDbDuringMigration_CompletesAndLogsWarning()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "kc-test-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var ps = new ProjectService(tmp);
            var logger = new CapturingLogger<DashboardService>();
            var svc = new DashboardService(ps, logger);

            // Seed the DB with a value that is parseable JSON but will fail both
            // current-format and legacy-format deserialization (scalar, not an array).
            var dbPath = ps.GetProjectDbPath("mig-proj");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS DashboardLayout (
                        Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                        LayoutJson TEXT NOT NULL DEFAULT '[]'
                    )
                    """;
                await create.ExecuteNonQueryAsync();
                await using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO DashboardLayout (Id, LayoutJson) VALUES (1, $j)";
                // Valid JSON but not a list → deserializing as List<T> throws JsonException.
                insert.Parameters.AddWithValue("$j", "\"not-an-array\"");
                await insert.ExecuteNonQueryAsync();
            }

            var dashDir = Path.Combine(tmp, ".dashboard");
            Directory.CreateDirectory(dashDir);

            // Must not throw.
            await svc.MigrateAsync("mig-proj", tmp);

            // Warning about the failed migration must have been emitted.
            Assert.True(logger.HasWarning("mig-proj"),
                "Expected Warning for mig-proj migration; got: " + string.Join("; ", logger.Entries.Select(e => e.Message)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
