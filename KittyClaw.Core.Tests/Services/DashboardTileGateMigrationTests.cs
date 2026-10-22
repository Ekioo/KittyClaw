using KittyClaw.Core.Services;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Tests.Services;

public sealed class DashboardTileGateMigrationTests
{
    [Fact]
    public async Task LegacyFileNameKey_IsMigratedWithoutLosingOtherTilesHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kittyclaw-tile-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "registry.db");
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE dashboard_tile_runs (
                    slug TEXT NOT NULL, fileName TEXT NOT NULL, lastFinishedAt TEXT NOT NULL,
                    PRIMARY KEY(slug, fileName));
                INSERT INTO dashboard_tile_runs VALUES ('sample', 'refreshed', '2026-01-01T00:00:00Z');
                INSERT INTO dashboard_tile_runs VALUES ('sample', 'preserved', '2026-01-02T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
            using var first = new DashboardTileGate(new ProjectService(directory));
            using var second = new DashboardTileGate(new ProjectService(directory));
            await Task.WhenAll(
                first.RunAsync("sample", "refreshed", false, _ => Task.CompletedTask, CancellationToken.None),
                second.RunAsync("sample", "new-tile", false, _ => Task.CompletedTask, CancellationToken.None));

            command.CommandText = "SELECT lastFinishedAt FROM dashboard_tile_runs WHERE slug='sample' AND tileSlug='preserved'";
            Assert.Equal("2026-01-02T00:00:00Z", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT COUNT(*) FROM dashboard_tile_runs";
            Assert.Equal(3L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT lastFinishedAt FROM dashboard_tile_runs WHERE tileSlug='refreshed'";
            Assert.NotEqual("2026-01-01T00:00:00Z", await command.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
