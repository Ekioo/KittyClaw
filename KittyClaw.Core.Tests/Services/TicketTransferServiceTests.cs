using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketTransferServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferToCriteriaRequiredColumn_IsAtomicAndSucceedsAfterAddingCriterion(bool requireValidated)
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        var columns = new ColumnService(projects);
        var todo = (await columns.ListColumnsAsync(target)).Single(c => c.Name == "Todo");
        await columns.UpdateColumnAsync(target, todo.Id, requireAcceptanceCriteria: !requireValidated, requireValidatedAcceptanceCriteria: requireValidated);
        var ticket = await tickets.CreateTicketAsync(source, "Protected transfer", status: "Todo");
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.TransferAsync(source, ticket.Id, target, "owner"));
        Assert.NotNull(await tickets.GetTicketAsync(source, ticket.Id));
        Assert.Empty(await tickets.ListTicketsAsync(target));
        var criterion = (await tickets.SaveAcceptanceCriterionAsync(source, ticket.Id, null, new SaveAcceptanceCriterionRequest("Behavior is verified")))!;
        if (requireValidated)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.TransferAsync(source, ticket.Id, target, "owner"));
            Assert.NotNull(await tickets.GetTicketAsync(source, ticket.Id));
            Assert.Empty(await tickets.ListTicketsAsync(target));
            await tickets.SaveAcceptanceCriterionAsync(source, ticket.Id, criterion.Id,
                new SaveAcceptanceCriterionRequest("Behavior is verified", AcceptanceStatus.PASS));
        }
        await transfers.TransferAsync(source, ticket.Id, target, "owner");
        Assert.Null(await tickets.GetTicketAsync(source, ticket.Id));
        Assert.Single((await tickets.GetTicketAsync(target, ticket.Id))!.AcceptanceCriteria);
    }

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
    public async Task Transfer_PreservesInternalDependenciesAndTheirTimestamps()
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        var root = await tickets.CreateTicketAsync(source, "Root");
        var dependent = await tickets.CreateTicketAsync(source, "Delivery", parentId: root.Id);
        var blocker = await tickets.CreateTicketAsync(source, "Prerequisite", parentId: root.Id);
        var dependency = await tickets.AddDependencyAsync(source, dependent.Id, blocker.Id);

        await transfers.TransferAsync(source, root.Id, target, "owner");

        var moved = (await tickets.GetTicketAsync(target, dependent.Id))!;
        Assert.Equal(blocker.Id, Assert.Single(moved.BlockedBy).TicketId);
        Assert.Equal(dependent.Id, Assert.Single((await tickets.GetTicketAsync(target, blocker.Id))!.Blocks).TicketId);
        await using var targetDb = projects.GetProjectDb(target);
        var saved = Assert.Single(targetDb.TicketDependencies);
        Assert.Equal(dependency.CreatedAt, saved.CreatedAt);
        await using var sourceDb = projects.GetProjectDb(source);
        Assert.Empty(sourceDb.TicketDependencies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_RejectsExternalDependenciesInEitherDirectionWithoutMutation(bool incoming)
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        var root = await tickets.CreateTicketAsync(source, "Root");
        var child = await tickets.CreateTicketAsync(source, "Child", parentId: root.Id);
        var outside = await tickets.CreateTicketAsync(source, "Outside tree");
        await tickets.AddDependencyAsync(source, incoming ? child.Id : outside.Id, incoming ? outside.Id : child.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.TransferAsync(source, root.Id, target, "owner"));

        Assert.Contains("split dependency", error.Message);
        Assert.Contains($"#{outside.Id}", error.Message);
        Assert.NotNull(await tickets.GetTicketAsync(source, root.Id));
        Assert.NotNull(await tickets.GetTicketAsync(source, child.Id));
        Assert.Null(await tickets.GetTicketAsync(target, root.Id));
        await using var sourceDb = projects.GetProjectDb(source);
        Assert.Single(sourceDb.TicketDependencies);
        await using var targetDb = projects.GetProjectDb(target);
        Assert.Empty(targetDb.Tickets);
        Assert.Empty(targetDb.ActivityEntries);
        Assert.Empty(targetDb.TicketDependencies);
    }

    [Fact]
    public async Task Transfer_RollsBackTicketsAndDependenciesWhenDependencyWriteFails()
    {
        using var temp = new TempDir();
        var (transfers, tickets, projects, source, target) = await BuildSut(temp);
        var root = await tickets.CreateTicketAsync(source, "Root");
        var child = await tickets.CreateTicketAsync(source, "Child", parentId: root.Id);
        await tickets.AddDependencyAsync(source, root.Id, child.Id);
        await using (var db = projects.GetProjectDb(target))
        {
            await TicketService.EnsureTicketDependenciesTableAsync(db);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_dependency BEFORE INSERT ON TicketDependencies BEGIN SELECT RAISE(ABORT, 'synthetic dependency failure'); END;");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.TransferAsync(source, root.Id, target, "owner"));

        Assert.Equal(child.Id, Assert.Single((await tickets.GetTicketAsync(source, root.Id))!.BlockedBy).TicketId);
        Assert.NotNull(await tickets.GetTicketAsync(source, child.Id));
        await using var targetDb = projects.GetProjectDb(target);
        Assert.Empty(targetDb.Tickets);
        Assert.Empty(targetDb.TicketDependencies);
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

        var criterion = await tickets.SaveAcceptanceCriterionAsync(source, root.Id, null,
            new("Transfer retains proof", AcceptanceStatus.PASS, new("Verified")));
        var createdAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updatedAt = createdAt.AddHours(1);
        await using (var db = projects.GetProjectDb(source))
        {
            var row = await db.Tickets.FindAsync(root.Id);
            row!.CreatedAt = createdAt;
            row.UpdatedAt = updatedAt;
            row.FireAt = createdAt.AddDays(3);
            row.ScheduleTarget = "Review";
            row.ColumnVisit = 7;
            (await db.Tickets.FindAsync(child.Id))!.ColumnVisit = 11;
            row.AgentTokens = 1234;
            row.AgentCostUsd = 4.56;
            await db.SaveChangesAsync();
        }

        var result = await transfers.TransferAsync(source, root.Id, target, "owner");
        Assert.Equal(criterion, Assert.Single((await tickets.GetTicketAsync(target, root.Id))!.AcceptanceCriteria));

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
        Assert.Equal(7L, moved.ColumnVisit);
        Assert.Equal(1234, moved.AgentTokens);
        Assert.Equal(4.56, moved.AgentCostUsd);
        Assert.Contains(moved.Comments, c => c.Content == "A preserved comment");
        Assert.Contains(moved.Activities, a => a.Text == "Manual audit entry");
        Assert.Contains(moved.Activities, a => a.Text.Contains("Transferred from project 'source' to 'target'"));
        Assert.Single(moved.Labels, l => l.Name == "critical" && l.Color == "#f00");
        var movedChild = await tickets.GetTicketAsync(target, child.Id);
        Assert.Equal(root.Id, movedChild!.ParentId);
        Assert.Equal(11L, movedChild.ColumnVisit);
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
    public async Task Transfer_AllocatesAuditIdsBeyondBothProjectsSoSequentialTransfersRemainLossless()
    {
        using var temp = new TempDir();
        var (transfers, tickets, _, source, target) = await BuildSut(temp);
        var first = await tickets.CreateTicketAsync(source, "First");
        var second = await tickets.CreateTicketAsync(source, "Second");
        var firstOriginalActivityId = Assert.Single((await tickets.GetTicketAsync(source, first.Id))!.Activities).Id;
        var secondOriginalActivityId = Assert.Single((await tickets.GetTicketAsync(source, second.Id))!.Activities).Id;

        await transfers.TransferAsync(source, first.Id, target, "owner");
        await transfers.TransferAsync(source, second.Id, target, "owner");

        var movedFirst = await tickets.GetTicketAsync(target, first.Id);
        var movedSecond = await tickets.GetTicketAsync(target, second.Id);
        Assert.NotNull(movedFirst);
        Assert.NotNull(movedSecond);
        Assert.Contains(movedFirst.Activities, activity => activity.Id == firstOriginalActivityId);
        Assert.Contains(movedSecond.Activities, activity => activity.Id == secondOriginalActivityId);

        var auditIds = movedFirst.Activities.Concat(movedSecond.Activities)
            .Where(activity => activity.Text.StartsWith("Transferred from project", StringComparison.Ordinal))
            .Select(activity => activity.Id)
            .ToList();
        Assert.Equal(2, auditIds.Count);
        Assert.All(auditIds, id => Assert.True(id > Math.Max(firstOriginalActivityId, secondOriginalActivityId)));
        Assert.Equal(2, auditIds.Distinct().Count());
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
