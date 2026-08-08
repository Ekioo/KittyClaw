using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Adversarial QA scenarios for the TicketDependency migration.
/// Exercises edge cases the programmer's tests do not cover.
/// Removed after QA pass.
/// </summary>
public sealed class TicketDependencyAdversarialTests
{
    [Fact]
    public async Task SelfReferentialEdge_IsAllowed_BySchema()
    {
        // Schema has no constraint forbidding (X blocks X).
        // Application-layer validation is an API concern (ticket #196).
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adv-self");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var t1 = await tickets.CreateTicketAsync(project.Slug, "A");

        await using var db = projects.GetProjectDb(project.Slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);
        var now = DateTime.UtcNow.ToString("o");

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES ({t1.Id}, {t1.Id}, {now})");

        var count = await db.TicketDependencies.CountAsync();
        Assert.Equal(1, count); // Schema allows; validation is API-layer concern.
    }

    [Fact]
    public async Task NullBlockedTicketId_IsRejected()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adv-null-blocked");
        await using var db = projects.GetProjectDb(project.Slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);

        var now = DateTime.UtcNow.ToString("o");
        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES (NULL, 1, '{now}')"));
        Assert.Contains("NOT NULL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullBlocksTicketId_IsRejected()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adv-null-blocks");
        await using var db = projects.GetProjectDb(project.Slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);

        var now = DateTime.UtcNow.ToString("o");
        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES (1, NULL, '{now}')"));
        Assert.Contains("NOT NULL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrationOnDbWithExistingData_PreservesData()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adv-existing-data");

        await using var db1 = projects.GetProjectDb(project.Slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db1);
        var now = DateTime.UtcNow.ToString("o");
        await db1.Database.ExecuteSqlRawAsync(
            $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES (1, 2, '{now}')");

        await using var db2 = projects.GetProjectDb(project.Slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db2); // second migration run on existing data

        var count = await db2.TicketDependencies.CountAsync();
        Assert.Equal(1, count); // Row must still be there after re-migration
    }

    [Fact]
    public async Task UniqueConstraint_IsOnOrderedPair_NotUnordered()
    {
        // (A,B) and (B,A) are distinct ordered pairs — both valid.
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adv-ordered");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var t1 = await tickets.CreateTicketAsync(project.Slug, "X");
        var t2 = await tickets.CreateTicketAsync(project.Slug, "Y");

        await using var db = projects.GetProjectDb(project.Slug);
        await TicketService.EnsureTicketDependenciesTableAsync(db);
        var now = DateTime.UtcNow.ToString("o");

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES ({t1.Id}, {t2.Id}, {now})");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO TicketDependencies (BlockedTicketId, BlocksTicketId, CreatedAt) VALUES ({t2.Id}, {t1.Id}, {now})");

        var count = await db.TicketDependencies.CountAsync();
        Assert.Equal(2, count);
    }
}
