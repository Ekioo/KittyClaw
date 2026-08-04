using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Duplicate column names crash the board (ToDictionary by name) and make ticket Status
/// ambiguous. These tests cover the self-healing migration that dedupes pre-existing
/// duplicates, the unique index that prevents new ones, and the idempotent create/rename
/// guards in ColumnService.
/// </summary>
public sealed class DuplicateColumnTests
{
    private static (ColumnService columns, string slug, string dbPath) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("dup-col-test").GetAwaiter().GetResult();
        var columns = new ColumnService(projects);
        var dbPath = Path.Combine(tmp.Path, "projects", $"{project.Slug}.db");
        return (columns, project.Slug, dbPath);
    }

    [Fact]
    public async Task BoardWithPreexistingDuplicates_IsHealedOnFirstRead()
    {
        using var tmp = new TempDir();
        var (columns, slug, dbPath) = BuildSut(tmp);

        // Simulate a board corrupted before the unique index existed: hand-create the
        // table (no index) with a duplicated "Scheduled" row, before any service call.
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TABLE IF EXISTS BoardColumns;
                CREATE TABLE BoardColumns (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Color TEXT NOT NULL DEFAULT '#5a6a80',
                    SortOrder INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO BoardColumns (Name, Color, SortOrder) VALUES
                    ('Backlog', '#5a6a80', 0),
                    ('Blocked', '#f06b6b', 1),
                    ('Scheduled', '#eab308', 2),
                    ('Scheduled', '#eab308', 2),
                    ('Done', '#3ecf8e', 3);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var names = (await columns.ListColumnsAsync(slug)).Select(c => c.Name).ToList();

        Assert.Single(names, n => n == "Scheduled");
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task CreateColumn_WithExistingName_ReturnsExistingInsteadOfDuplicating()
    {
        using var tmp = new TempDir();
        var (columns, slug, _) = BuildSut(tmp);

        var first = (await columns.ListColumnsAsync(slug)).First(c => c.Name == "Todo");
        var created = await columns.CreateColumnAsync(slug, "Todo", "#123456");

        Assert.Equal(first.Id, created.Id);
        var names = (await columns.ListColumnsAsync(slug)).Select(c => c.Name).ToList();
        Assert.Single(names, n => n == "Todo");
    }

    [Fact]
    public async Task ConcurrentFirstReads_SeedOneCoherentDefaultBoard()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("concurrent-seed");
        var readers = Enumerable.Range(0, 12)
            .Select(_ => new ColumnService(projects).ListColumnsAsync(project.Slug));

        var results = await Task.WhenAll(readers);

        Assert.All(results, columns => Assert.Equal(7, columns.Count));
        Assert.All(results, columns => Assert.Equal(columns.Count,
            columns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
    }

    [Fact]
    public async Task RenameColumn_ToTakenName_IsRefused()
    {
        using var tmp = new TempDir();
        var (columns, slug, _) = BuildSut(tmp);

        var todo = (await columns.ListColumnsAsync(slug)).First(c => c.Name == "Todo");
        var renamed = await columns.UpdateColumnAsync(slug, todo.Id, name: "Done");

        Assert.Null(renamed);
        var names = (await columns.ListColumnsAsync(slug)).Select(c => c.Name).ToList();
        Assert.Single(names, n => n == "Done");
        Assert.Single(names, n => n == "Todo");
    }
}
