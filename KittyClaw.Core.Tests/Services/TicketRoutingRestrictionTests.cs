using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketRoutingRestrictionTests
{
    [Fact]
    public void Policy_ReusesEveryProcessorRoutingDestination()
    {
        var processor = new ColumnProcessor
        {
            ColumnId = 1,
            Name = "Review",
            DefaultTargetColumnId = 2,
            TechnicalFailureColumnId = 3,
            Routes = [new("approved", 4)],
            BeforeActions = [new("http", new HttpRequestActionSpec { Url = "https://example.test" }, 5)],
        };

        var policy = ColumnRoutingPolicy.From(processor);

        Assert.True(policy.IsRestricted);
        Assert.Equal([2, 3, 4, 5], policy.AllowedTargetColumnIds.Order());
    }

    [Fact]
    public async Task ManualMove_IsLimitedByRouting_WhileEngineMoveCanBypassTheGuard()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Routed moves");
        var members = new MemberService(projects);
        var skills = new ProjectSkillService(projects);
        var processors = new ColumnProcessorService(projects, skills);
        var columns = new ColumnService(projects, processors);
        var boardColumns = await columns.ListColumnsAsync(project.Slug);
        var todo = boardColumns.Single(column => column.Name == "Todo");
        var inProgress = boardColumns.Single(column => column.Name == "InProgress");
        var review = boardColumns.Single(column => column.Name == "Review");
        var tickets = new TicketService(projects, members, processors);

        await processors.SaveAsync(
            project.Slug, todo.Id, "Todo processor", "Process Todo.", null,
            true, 20, [], [], [], defaultTargetColumnId: inProgress.Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Respect workflow", status: todo.Name);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tickets.UpdateTicketAsync(project.Slug, ticket.Id, status: review.Name, enforceRouting: true));
        Assert.Contains("n'autorise pas", error.Message);

        var allowed = await tickets.UpdateTicketAsync(
            project.Slug, ticket.Id, status: inProgress.Name, enforceRouting: true);
        Assert.Equal(inProgress.Id, allowed!.ColumnId);

        var engineTicket = await tickets.CreateTicketAsync(project.Slug, "Engine route", status: todo.Name);
        var bypassed = await tickets.UpdateTicketAsync(
            project.Slug, engineTicket.Id, author: "automation", status: review.Name, enforceRouting: false);
        Assert.Equal(review.Id, bypassed!.ColumnId);
    }

    [Fact]
    public void ColumnWithoutDestinations_RemainsUnrestricted()
    {
        var policy = ColumnRoutingPolicy.From(new ColumnProcessor { ColumnId = 1, Name = "Draft" });

        Assert.False(policy.IsRestricted);
        Assert.True(policy.Allows(1, 99));
    }
}
