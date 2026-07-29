using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Non-regression tests for ticket #135: signal-path firings carry only the ticket id
/// (TriggerFiring(id, null, null)), so ticketInColumn conditions evaluated against
/// firing.TicketStatus were ALWAYS false — event-driven automations with a column condition
/// silently never fired via the fast path. The condition must resolve the live status.
/// </summary>
public class SignalPathConditionTests
{
    private sealed record Harness(
        ActionExecutor Executor,
        TriggerHandler Handler,
        ProjectRuntimeManager Manager,
        TicketService Tickets,
        AutomationStore Store,
        string Slug,
        string Workspace);

    private static async Task<Harness> BuildAsync(string dataDir)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("signal-cond-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4), NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var loc = new LocalizationService(new AppSettingsService(dataDir));
        var tickets = new TicketService(projects, members);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance), NullLogger.Instance);

        var store = new AutomationStore(projects);
        var manager = new ProjectRuntimeManager(store, new TriggerStateStore(projects), NullLogger.Instance);
        var handler = new TriggerHandler(
            projects, manager, executor, tickets, members, sessions, runs, NullLogger.Instance);

        return new Harness(executor, handler, manager, tickets, store, project.Slug, workspace);
    }

    private static AutomationRule ColumnConditionAutomation(params string[] columns) => new()
    {
        Id = "on-comment",
        Trigger = new TicketCommentAddedTriggerSpec(),
        Conditions = [new TicketInColumnConditionSpec { Columns = columns.ToList() }],
        Actions = [],
    };

    private static ProjectRuntime Runtime(Harness h) => new(h.Slug)
    {
        Workspace = h.Workspace,
        Config = new AutomationConfig(),
    };

    [Fact]
    public async Task SignalFiring_WithoutStatus_PassesColumnCondition_WhenTicketInColumn()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "InProgress");

        // Signal firings carry only the ticket id — the historical always-false case.
        var firing = new TriggerFiring(ticket.Id, null, null);

        Assert.True(await h.Executor.ConditionsMatchAsync(
            Runtime(h), ColumnConditionAutomation("InProgress"), firing));
    }

    [Fact]
    public async Task SignalFiring_WithoutStatus_FailsColumnCondition_WhenTicketElsewhere()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Backlog");

        Assert.False(await h.Executor.ConditionsMatchAsync(
            Runtime(h), ColumnConditionAutomation("InProgress"), new TriggerFiring(ticket.Id, null, null)));
    }

    [Fact]
    public async Task TicketlessFiring_FailsColumnCondition_WithoutThrowing()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        Assert.False(await h.Executor.ConditionsMatchAsync(
            Runtime(h), ColumnConditionAutomation("InProgress"), new TriggerFiring(null, null, null)));
    }

    [Fact]
    public async Task PollFiring_WithStatusSnapshot_StillEvaluatesWithoutLookup()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        // No ticket in DB: a poll-style firing carrying its own status snapshot must not
        // need (or trigger) a live lookup.
        Assert.True(await h.Executor.ConditionsMatchAsync(
            Runtime(h), ColumnConditionAutomation("Review"), new TriggerFiring(999, "T", "Review")));
    }

    // ── End-to-end: the fast path actually dispatches now ───────────────────

    private static AutomationConfig ReplyBotConfig(int pollSeconds) => new()
    {
        Automations =
        {
            new AutomationRule
            {
                Id = "reply-bot",
                Trigger = new TicketCommentAddedTriggerSpec { PollSeconds = pollSeconds, Authors = { "owner" } },
                Conditions = [new TicketInColumnConditionSpec { Columns = { "InProgress" } }],
                Actions = [new AddCommentActionSpec { Content = "ack", Author = "bot" }],
            },
        },
    };

    private static async Task<int> BotCommentCountAsync(Harness h, int ticketId)
    {
        var ticket = await h.Tickets.GetTicketAsync(h.Slug, ticketId);
        return ticket!.Comments.Count(c => c.Author == "bot");
    }

    [Fact]
    public async Task SignalDispatch_PassesColumnCondition_WhilePollIsDormant()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        // Poll every hour: after the warm-up tick, any dispatch can only come from the signal path.
        await h.Store.SaveAsync(h.Slug, ReplyBotConfig(pollSeconds: 3600));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "InProgress");
        await h.Handler.ProcessTickAsync(CancellationToken.None); // warm-up poll, then dormant

        var comment = await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "please check", "owner");
        await h.Manager.NotifySignalAsync(h.Slug,
            new CommentAddedSignal(ticket.Id, comment!.Id, "owner", "please check"));
        await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(1, await BotCommentCountAsync(h, ticket.Id));
    }

    [Fact]
    public async Task SignalWithFailingConditions_IsRetriedByPoll_OnceConditionsPass()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        // Dormant poll: the signal is the only active path at first (ticket #136 semantics).
        await h.Store.SaveAsync(h.Slug, ReplyBotConfig(pollSeconds: 3600));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Backlog");
        await h.Handler.ProcessTickAsync(CancellationToken.None); // warm-up seed scan

        // Signal arrives while the column condition fails → dropped, but NOT consumed.
        var comment = await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "please check", "owner");
        await h.Manager.NotifySignalAsync(h.Slug,
            new CommentAddedSignal(ticket.Id, comment!.Id, "owner", "please check"));
        await h.Handler.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(0, await BotCommentCountAsync(h, ticket.Id));

        // Conditions become true; a reload swaps the trigger instance (pending map lost) —
        // the persisted cursor must still hold the comment as unconsumed so the poll fires it.
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "InProgress", "owner");
        await h.Store.SaveAsync(h.Slug, ReplyBotConfig(pollSeconds: 0));
        await h.Manager.ReloadProjectAsync(h.Slug);
        await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(1, await BotCommentCountAsync(h, ticket.Id));
    }

    [Fact]
    public async Task SignalDispatch_ThenPolls_DoesNotDoubleFire()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        // Poll on every tick: without the #113 dedup, the poll would immediately re-fire
        // the comment the signal path just dispatched.
        await h.Store.SaveAsync(h.Slug, ReplyBotConfig(pollSeconds: 0));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "InProgress");
        await h.Handler.ProcessTickAsync(CancellationToken.None); // silent seed scan

        var comment = await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "please check", "owner");
        await h.Manager.NotifySignalAsync(h.Slug,
            new CommentAddedSignal(ticket.Id, comment!.Id, "owner", "please check"));
        for (var tick = 0; tick < 3; tick++)
            await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(1, await BotCommentCountAsync(h, ticket.Id));
    }
}
