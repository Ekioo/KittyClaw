using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketAcceptanceCriteriaTests
{
    [Fact]
    public async Task CriteriaPersistAndUpdateIndependentlyThroughMcp()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var slug = (await projects.CreateProjectAsync("acceptance")).Slug;
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var tools = new McpTools(projects, tickets, new ColumnService(projects), members, new BoardUpdateNotifier());
        var ticket = await tickets.CreateTicketAsync(slug, "Feature");
        var first = await tools.SaveAcceptanceCriterion(slug, ticket.Id, new("Renders", Evidence: new("{\"count\":1}", "application/json")));
        var second = await tools.SaveAcceptanceCriterion(slug, ticket.Id, new("Saves"));
        Assert.Equal(AcceptanceStatus.NotEvaluated, first.Status);
        foreach (var status in new[] { AcceptanceStatus.PASS, AcceptanceStatus.FAIL, AcceptanceStatus.PARTIAL, AcceptanceStatus.NotEvaluated })
        {
            await tools.SaveAcceptanceCriterion(slug, ticket.Id, new(first.Text, status, first.Evidence), first.Id);
            var read = await tools.GetTicket(slug, ticket.Id);
            Assert.Equal(status, read.AcceptanceCriteria.Single(c => c.Id == first.Id).Status);
            Assert.Equal(first.Evidence, read.AcceptanceCriteria.Single(c => c.Id == first.Id).Evidence);
            Assert.Equal(second, read.AcceptanceCriteria.Single(c => c.Id == second.Id));
        }
        Assert.True(await tools.DeleteAcceptanceCriterion(slug, ticket.Id, first.Id));
        Assert.Equal(second, Assert.Single((await tools.GetTicket(slug, ticket.Id)).AcceptanceCriteria));
        var other = await tickets.CreateTicketAsync(slug, "Other");
        Assert.Null(await tickets.SaveAcceptanceCriterionAsync(slug, other.Id, second.Id, new("Wrong ticket")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tickets.SaveAcceptanceCriterionAsync(slug, ticket.Id, null, new("Invalid JSON", Evidence: new("{", "application/json"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tickets.SaveAcceptanceCriterionAsync(slug, ticket.Id, null, new("Invalid status", (AcceptanceStatus)99)));
    }

    [Fact]
    public void ChangesInvalidateRoutingFingerprint()
    {
        var ticket = new Ticket { Id = 1, Title = "Feature" };
        var processor = new ColumnProcessor { ColumnId = 1, Name = "Router" };
        var before = JevQualificationService.Fingerprint(processor, ticket, []);
        ticket.AcceptanceCriteria = [new("criterion", "Works", AcceptanceStatus.FAIL, new("A test failed"))];
        var failed = JevQualificationService.Fingerprint(processor, ticket, []);
        Assert.NotEqual(before, failed);
        ticket.AcceptanceCriteria = [new("criterion", "Works", AcceptanceStatus.PASS, new("All tests passed"))];
        Assert.NotEqual(failed, JevQualificationService.Fingerprint(processor, ticket, []));
        Assert.Contains("Works", JevWorkReviewService.AcceptanceCriteria(processor, ticket));
        var prefix = new string('x', 5000);
        ticket.AcceptanceCriteria = [new("criterion", "Works", AcceptanceStatus.PASS, new(prefix + "a"))];
        var originalProof = JevQualificationService.Fingerprint(processor, ticket, []);
        ticket.AcceptanceCriteria = [new("criterion", "Works", AcceptanceStatus.PASS, new(prefix + "b"))];
        Assert.NotEqual(originalProof, JevQualificationService.Fingerprint(processor, ticket, []));
        var image = new TicketAcceptanceCriterion("image", "Screenshot", Evidence: new("BASE64PAYLOAD", "image/png"));
        Assert.DoesNotContain("BASE64PAYLOAD", System.Text.Json.JsonSerializer.Serialize(image.ToRoutingContext()));
    }

    [Fact]
    public async Task LegacyDatabaseGetsCriteriaColumnBeforeTicketQueries()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var path = projects.GetProjectDbPath("legacy");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Tickets (Id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }
        using var db = projects.GetProjectDb("legacy");
        await using var check = new SqliteConnection($"Data Source={path}");
        await check.OpenAsync();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Tickets') WHERE name = 'AcceptanceCriteriaJson'";
        Assert.Equal(1L, await query.ExecuteScalarAsync());
    }
}
