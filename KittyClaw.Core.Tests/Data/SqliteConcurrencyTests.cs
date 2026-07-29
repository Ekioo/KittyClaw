using KittyClaw.Core.Data;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Data;

/// <summary>
/// Verifies the SQLite concurrency pragmas (busy_timeout + WAL) are applied on every EF
/// connection, and that parallel writers on the same project database no longer trip
/// "database is locked".
/// </summary>
public class SqliteConcurrencyTests
{
    private static (long BusyTimeout, string JournalMode) ReadPragmas(DbContext ctx)
    {
        ctx.Database.OpenConnection();
        var conn = ctx.Database.GetDbConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        var busy = (long)cmd.ExecuteScalar()!;
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string)cmd.ExecuteScalar()!;
        return (busy, mode);
    }

    [Fact]
    public void ProjectContext_AppliesBusyTimeoutAndWal()
    {
        using var tmp = new TempDir();
        using var ctx = new TodoDbContext(Path.Combine(tmp.Path, "project.db"));

        var (busy, mode) = ReadPragmas(ctx);

        Assert.Equal(5000, busy);
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public void RegistryContext_AppliesBusyTimeoutAndWal()
    {
        using var tmp = new TempDir();
        using var ctx = new RegistryDbContext(Path.Combine(tmp.Path, "registry.db"));

        var (busy, mode) = ReadPragmas(ctx);

        Assert.Equal(5000, busy);
        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task ParallelWriters_OnSameProjectDb_AllSucceed()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("sqlite-concurrency-test");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Contended ticket", status: "Todo");

        // Each call opens its own connection: without busy_timeout + WAL, concurrent
        // writers on the same file intermittently threw "database is locked".
        var writers = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            await tickets.AddCommentAsync(project.Slug, ticket.Id, $"comment {i}", "owner");
            await tickets.CreateTicketAsync(project.Slug, $"Ticket {i}", status: "Todo");
        }));
        await Task.WhenAll(writers);

        var after = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.Equal(16, after!.Comments.Count);
        Assert.Equal(17, (await tickets.ListTicketsAsync(project.Slug)).Count);
    }
}
