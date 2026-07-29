using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Non-regression tests for ticket #112: two ticketInColumn automations watching the same
/// column must not both process the same ticket in one tick. Automations are evaluated in
/// file order and the first one that matches AND dispatches consumes the ticket for that
/// tick (first-match-wins). A ticket the first automation does not match (trigger filter or
/// conditions) stays available to later automations in the same tick.
/// </summary>
public class ColumnPollFirstMatchTests
{
    private sealed record Harness(
        TriggerHandler Handler,
        TicketService Tickets,
        MemberService Members,
        AutomationStore Store,
        string Slug);

    private static async Task<Harness> BuildAsync(string dataDir, params AutomationRule[] automations)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("first-match-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 4);
        var runner = new AgentRunner(sessions, runs, gate, NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var loc = new LocalizationService(new AppSettingsService(dataDir));
        var tickets = new TicketService(projects, members);
        var runState = new RunStateManager(runs, cost, tickets, NullLogger.Instance);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState,
            NullLogger.Instance);

        var store = new AutomationStore(projects);
        await store.SaveAsync(project.Slug, new AutomationConfig { Automations = automations.ToList() });
        var manager = new ProjectRuntimeManager(store, new TriggerStateStore(projects), NullLogger.Instance);
        var handler = new TriggerHandler(
            projects, manager, executor, tickets, members, sessions, runs, NullLogger.Instance);

        return new Harness(handler, tickets, members, store, project.Slug);
    }

    private static AutomationRule ColumnPollAutomation(string id, string comment, string? assigneeFilter = null,
        ConditionSpec? condition = null) => new()
    {
        Id = id,
        Name = id,
        Trigger = new TicketInColumnTriggerSpec { Columns = { "Todo" }, Seconds = 30, AssigneeSlug = assigneeFilter },
        Conditions = condition is null ? [] : [condition],
        Actions = [new AddCommentActionSpec { Content = comment, Author = "automation" }],
    };

    [Fact]
    public async Task SameTicket_MatchingTwoColumnPollAutomations_OnlyFirstDispatches()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first"),
            ColumnPollAutomation("second", "from-second"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Racy ticket", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        var comment = Assert.Single(after!.Comments);
        Assert.Equal("from-first", comment.Content);
    }

    [Fact]
    public async Task TicketNotMatchedByFirstTriggerFilter_StaysAvailableToSecond()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first", assigneeFilter: "ghost"),
            ColumnPollAutomation("second", "from-second"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Unassigned ticket", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        var comment = Assert.Single(after!.Comments);
        Assert.Equal("from-second", comment.Content);
    }

    [Fact]
    public async Task TicketFailingFirstAutomationConditions_IsNotConsumed()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first",
                condition: new AssignedToConditionSpec { Slugs = { "ghost" } }),
            ColumnPollAutomation("second", "from-second"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Condition-miss ticket", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var after = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        var comment = Assert.Single(after!.Comments);
        Assert.Equal("from-second", comment.Content);
    }

    [Fact]
    public async Task DistinctTickets_AreConsumedIndependently()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first", assigneeFilter: "worker"),
            ColumnPollAutomation("second", "from-second"));
        await h.Members.CreateMemberAsync(h.Slug, "worker");
        var assigned = await h.Tickets.CreateTicketAsync(h.Slug, "Assigned", status: "Todo", assignedTo: "worker");
        var unassigned = await h.Tickets.CreateTicketAsync(h.Slug, "Unassigned", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var t1 = await h.Tickets.GetTicketAsync(h.Slug, assigned.Id);
        Assert.Equal("from-first", Assert.Single(t1!.Comments).Content);
        var t2 = await h.Tickets.GetTicketAsync(h.Slug, unassigned.Id);
        Assert.Equal("from-second", Assert.Single(t2!.Comments).Content);
    }
}
