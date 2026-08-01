using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketTransferServiceTests
{
    private static async Task<(TicketTransferService transfers, TicketService tickets, ProjectService projects, string source, string target)> BuildSut(TempDir temp)
    {
        var projects = new ProjectService(temp.Path);
        var source = await projects.CreateProjectAsync("source");
        var target = await projects.CreateProjectAsync("target");
        var members = new MemberService(projects);
        var ticketService = new TicketService(projects, members);
        var columnService = new ColumnService(projects);
        var labelService = new LabelService(projects);
        await columnService.ListColumnsAsync(source.Slug);
        await columnService.ListColumnsAsync(target.Slug);
        return (new TicketTransferService(projects, ticketService, columnService, members, labelService), ticketService, projects, source.Slug, target.Slug);
    }

    [Fact]
    public async Task Transfer_PreservesCompleteTicketTreeAndAuditData()
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        var labels = new LabelService(projects);
        var members = new MemberService(projects);
        await members.CreateMemberAsync(source, "Programmer");
        await members.CreateMemberAsync(target, "Programmer");
        var label = await labels.CreateLabelAsync(source, "critical", "#f00");
        var root = await tickets.CreateTicketAsync(source, "Root", "Detailed body", "owner", "Scheduled", [label.Id], TicketPriority.Critical, "programmer");
        var child = await tickets.CreateTicketAsync(source, "Child", parentId: root.Id);
        await tickets.AddCommentAsync(source, root.Id, "A preserved comment", "owner");
        await tickets.AddActivityAsync(source, root.Id, "Manual audit entry", "programmer");

        var createdAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updatedAt = createdAt.AddHours(1);
        await using (var db = projects.GetProjectDb(source))
        {
            var row = await db.Tickets.FindAsync(root.Id);
            row!.CreatedAt = createdAt;
            row.UpdatedAt = updatedAt;
            row.FireAt = createdAt.AddDays(3);
            row.ScheduleTarget = "Review";
            row.AgentTokens = 1234;
            row.AgentCostUsd = 4.56;
            await db.SaveChangesAsync();
        }

        var result = await transfers.TransferAsync(source, root.Id, target, "owner");

        Assert.Equal(root.Id, result.TicketId);
        Assert.Equal(2, result.TicketCount);
        Assert.Null(await tickets.GetTicketAsync(source, root.Id));
        var moved = await tickets.GetTicketAsync(target, root.Id);
        Assert.NotNull(moved);
        Assert.Equal("Detailed body", moved.Description);
        Assert.Equal(TicketPriority.Critical, moved.Priority);
        Assert.Equal("Scheduled", moved.Status);
        Assert.Equal(createdAt, moved.CreatedAt);
        Assert.Equal(updatedAt, moved.UpdatedAt);
        Assert.Equal(createdAt.AddDays(3), moved.FireAt);
        Assert.Equal("Review", moved.ScheduleTarget);
        Assert.Equal(1234, moved.AgentTokens);
        Assert.Equal(4.56, moved.AgentCostUsd);
        Assert.Contains(moved.Comments, c => c.Content == "A preserved comment");
        Assert.Contains(moved.Activities, a => a.Text == "Manual audit entry");
        Assert.Contains(moved.Activities, a => a.Text.Contains("Transferred from project 'source' to 'target'"));
        Assert.Single(moved.Labels, l => l.Name == "critical" && l.Color == "#f00");
        var movedChild = await tickets.GetTicketAsync(target, child.Id);
        Assert.Equal(root.Id, movedChild!.ParentId);
    }

    [Fact]
    public async Task Transfer_RejectsMissingAssigneeWithoutMovingEitherTicket()
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        await new MemberService(projects).CreateMemberAsync(source, "Programmer");
        var ticket = await tickets.CreateTicketAsync(source, "Needs mapping", assignedTo: "programmer");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.TransferAsync(source, ticket.Id, target, "owner"));

        Assert.Contains("missing assignees", error.Message);
        Assert.NotNull(await tickets.GetTicketAsync(source, ticket.Id));
        Assert.Null(await tickets.GetTicketAsync(target, ticket.Id));
    }

    [Fact]
    public async Task Transfer_RejectsIdentifierCollisionWithoutMutation()
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        var sourceTicket = await tickets.CreateTicketAsync(source, "Source");
        var targetTicket = await tickets.CreateTicketAsync(target, "Existing");
        Assert.Equal(sourceTicket.Id, targetTicket.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.TransferAsync(source, sourceTicket.Id, target, "owner"));

        Assert.NotNull(await tickets.GetTicketAsync(source, sourceTicket.Id));
        Assert.Equal("Existing", (await tickets.GetTicketAsync(target, targetTicket.Id))!.Title);
    }

    [Fact]
    public async Task Transfer_RollsBackBothDatabasesWhenTargetWriteFails()
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        var ticket = await tickets.CreateTicketAsync(source, "explode");
        await using (var connection = new SqliteConnection($"Data Source={projects.GetProjectDbPath(target)}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_transfer BEFORE INSERT ON Tickets WHEN NEW.Title = 'explode' BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.TransferAsync(source, ticket.Id, target, "owner"));

        Assert.Contains("both projects were left unchanged", error.Message);
        Assert.NotNull(await tickets.GetTicketAsync(source, ticket.Id));
        Assert.Null(await tickets.GetTicketAsync(target, ticket.Id));
    }
}
