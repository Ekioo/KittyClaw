using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

public class AutomationQueueStoreTests
{
    private static AutomationRule Rule(string id) => new()
    {
        Id = id,
        Name = $"Rule {id}",
        Trigger = new TicketInColumnTriggerSpec { Columns = ["Todo"] },
        Actions = [new AddCommentActionSpec { Content = id }],
    };

    private static async Task<(AutomationQueueStore Queue, TicketService Tickets, string Slug)> BuildAsync(string root)
    {
        var projects = new ProjectService(root);
        var project = await projects.CreateProjectAsync("queue-test");
        return (new AutomationQueueStore(projects), new TicketService(projects, new MemberService(projects)), project.Slug);
    }

    [Fact]
    public async Task RepeatedPolling_DeduplicatesLogicalColumnOccurrence()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ticket", status: "Todo");

        for (var i = 0; i < 10; i++)
            await h.Queue.EnqueueAsync(h.Slug, ticket, [Rule("a"), Rule("b")]);

        var entries = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        Assert.Equal(2, entries.Count);
        Assert.Single(entries.Select(x => x.OccurrenceId).Distinct());
    }

    [Fact]
    public async Task ManyAutomationsInOneOccurrence_AreNotMistakenForAColumnLoop()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ticket", status: "Todo");
        var rules = Enumerable.Range(1, 5).Select(i => Rule($"rule-{i}")).ToArray();

        await h.Queue.EnqueueAsync(h.Slug, ticket, rules);

        var entries = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        Assert.Equal(5, entries.Count);
        Assert.All(entries, entry => Assert.Equal(AutomationQueueStatus.Pending, entry.Status));
        Assert.Single(entries.Select(entry => entry.OccurrenceId).Distinct());
    }

    [Fact]
    public async Task FourDistinctWorkflowColumns_AreNotMistakenForAColumnLoop()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ticket", status: "Backlog");

        foreach (var column in new[] { "Backlog", "Todo", "InProgress", "Review" })
        {
            ticket.Status = column;
            await h.Queue.ObserveColumnAsync(h.Slug, ticket.Id, column);
            await h.Queue.EnqueueAsync(h.Slug, ticket, [Rule(column)]);
        }

        var entries = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        Assert.Equal(4, entries.Count);
        Assert.All(entries, entry => Assert.Equal(AutomationQueueStatus.Pending, entry.Status));
    }

    [Fact]
    public async Task LeavingAndReenteringColumn_CreatesNewOccurrence()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ticket", status: "Todo");
        await h.Queue.EnqueueAsync(h.Slug, ticket, [Rule("a")]);
        ticket.Status = "Doing";
        await h.Queue.ObserveColumnAsync(h.Slug, ticket.Id, ticket.Status);
        ticket.Status = "Todo";
        await h.Queue.EnqueueAsync(h.Slug, ticket, [Rule("a")]);

        var entries = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries.Select(x => x.OccurrenceId).Distinct().Count());
    }

    [Fact]
    public async Task ExpiredRunningLease_IsRecoveredWithoutChangingFifoOrder()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var firstTicket = await h.Tickets.CreateTicketAsync(h.Slug, "First", status: "Todo");
        var secondTicket = await h.Tickets.CreateTicketAsync(h.Slug, "Second", status: "Todo");
        await h.Queue.EnqueueAsync(h.Slug, firstTicket, [Rule("a")]);
        await h.Queue.EnqueueAsync(h.Slug, secondTicket, [Rule("b")]);

        var firstClaim = await h.Queue.ClaimNextAsync(h.Slug, TimeSpan.FromMilliseconds(-1));
        var recovered = await h.Queue.ClaimNextAsync(h.Slug, TimeSpan.FromMinutes(1));

        Assert.Equal(firstClaim!.Id, recovered!.Id);
        Assert.Equal(2, recovered.Attempts);
        Assert.Equal(firstTicket.Id, recovered.TicketId);
    }

    [Fact]
    public async Task RestartedStore_RecoversExpiredClaimWithoutDuplicatingEntry()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("queue-restart-test");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Restart", status: "Todo");
        var beforeRestart = new AutomationQueueStore(projects);
        await beforeRestart.EnqueueAsync(project.Slug, ticket, [Rule("restart")]);
        var abandoned = await beforeRestart.ClaimNextAsync(project.Slug, TimeSpan.FromMilliseconds(-1));

        var afterRestart = new AutomationQueueStore(new ProjectService(tmp.Path));
        var recovered = await afterRestart.ClaimNextAsync(project.Slug, TimeSpan.FromMinutes(1));

        Assert.Equal(abandoned!.Id, recovered!.Id);
        Assert.Equal(2, recovered.Attempts);
        Assert.Single(await afterRestart.ListForTicketAsync(project.Slug, ticket.Id));
    }

    [Fact]
    public async Task TerminalState_PersistsReasonAndAdvancesQueue()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Ticket", status: "Todo");
        await h.Queue.EnqueueAsync(h.Slug, ticket, [Rule("a"), Rule("b")]);
        var first = await h.Queue.ClaimNextAsync(h.Slug, TimeSpan.FromMinutes(1));
        await h.Queue.FinishAsync(h.Slug, first!.Id, AutomationQueueStatus.Skipped, "Column changed.");
        var second = await h.Queue.ClaimNextAsync(h.Slug, TimeSpan.FromMinutes(1));

        Assert.Equal("b", second!.AutomationId);
        var history = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        var skipped = Assert.Single(history, x => x.Status == AutomationQueueStatus.Skipped);
        Assert.Equal("Column changed.", skipped.Reason);
    }
}
