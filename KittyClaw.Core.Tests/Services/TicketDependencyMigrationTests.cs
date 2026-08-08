using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketDependencyMigrationTests
{
    private static async Task<(TicketService tickets, string slug)> BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("dep-test");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        return (tickets, project.Slug);
    }

    [Fact]
    public async Task EnsureTicketDependenciesTable_CreatesTableOnFreshDatabase()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSut(tmp);

        // Use the internal migration entry-point via a freshly opened project db.
        var projects = new ProjectService(tmp.Path);
        await using var db = projects.GetProjectDb(slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);

        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' AND name='TicketDependencies'")
            .ToListAsync();
        Assert.Single(tables);
    }

    [Fact]
    public async Task EnsureTicketDependenciesTable_IsIdempotent()
    {
        using var tmp = new TempDir();
        var (_, slug) = await BuildSut(tmp);

        var projects = new ProjectService(tmp.Path);
        await using var db = projects.GetProjectDb(slug);

        // Running twice must not throw.
        await TicketService.EnsureTicketDependenciesTableAsync(db);
        await TicketService.EnsureTicketDependenciesTableAsync(db);

        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' AND name='TicketDependencies'")
            .ToListAsync();
        Assert.Single(tables);
    }

    [Fact]
    public async Task DuplicateEdge_IsRejectedAtDatabaseLevel()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSut(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");
        var t2 = await svc.CreateTicketAsync(slug, "B");

        var projects = new ProjectService(tmp.Path);
        await using var db = projects.GetProjectDb(slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);

        var now = DateTime.UtcNow.ToString("o");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES ({t1.Id}, {t2.Id}, {now})");

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            db.Database.ExecuteSqlAsync(
                $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES ({t1.Id}, {t2.Id}, {now})"));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BothIndexDirections_ExistAfterMigration()
    {
        using var tmp = new TempDir();
        var (_, slug) = await BuildSut(tmp);

        var projects = new ProjectService(tmp.Path);
        await using var db = projects.GetProjectDb(slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);

        var indexes = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='index' AND tbl_name='TicketDependencies'")
            .ToListAsync();

        Assert.Contains(indexes, i => i.Contains("BlockedTicketId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(indexes, i => i.Contains("BlocksTicketId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReverseEdge_IsAllowed()
    {
        using var tmp = new TempDir();
        var (svc, slug) = await BuildSut(tmp);
        var t1 = await svc.CreateTicketAsync(slug, "A");
        var t2 = await svc.CreateTicketAsync(slug, "B");

        var projects = new ProjectService(tmp.Path);
        await using var db = projects.GetProjectDb(slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);

        var now = DateTime.UtcNow.ToString("o");
        // (t1 blocked by t2) and (t2 blocked by t1) are two distinct ordered pairs — both valid.
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES ({t1.Id}, {t2.Id}, {now})");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES ({t2.Id}, {t1.Id}, {now})");

        var count = await db.TicketDependencies.CountAsync();
        Assert.Equal(2, count);
    }
}
