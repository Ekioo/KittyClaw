using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Ticket kittyclaw-front#211: a Todo ticket assigned to a board member without an agent
/// definition on disk (.agents/{slug}/SKILL.md) was skipped by the dispatch automation with
/// no trace — typically because the assignedTo allowlist condition filtered the firing before
/// anything logged. The warner must write a throttled SKIPPED line to .agents/channel/debug.log
/// and surface a one-time activity on the ticket.
/// </summary>
public class MissingAgentDefinitionWarnerTests
{
    private sealed record Harness(
        TriggerHandler Handler,
        TicketService Tickets,
        MemberService Members,
        LocalizationService Loc,
        string Workspace,
        string Slug);

    private static async Task<Harness> BuildAsync(string dataDir, params AutomationRule[] automations)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("missing-agent-test");
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
        await store.SaveAsync(project.Slug, new AutomationConfig { Automations = automations.ToList() });
        var manager = new ProjectRuntimeManager(store, new TriggerStateStore(projects), NullLogger.Instance);
        var handler = new TriggerHandler(
            projects, manager, executor, tickets, members, sessions, runs,
            new AutomationQueueStore(projects), loc, NullLogger.Instance);

        return new Harness(handler, tickets, members, loc, workspace, project.Slug);
    }

    /// <summary>Mirrors the production assignee-dispatch: {assignee} runAgent behind an
    /// assignedTo allowlist that does NOT include the ghost member (the #191 shape).</summary>
    private static AutomationRule AssigneeDispatchAutomation(params string[] allowedSlugs) => new()
    {
        Id = "assignee-dispatch",
        Name = "Dispatch: Todo ticket",
        Trigger = new TicketInColumnTriggerSpec { Columns = { "Todo" }, Seconds = 0 },
        Conditions = [new AssignedToConditionSpec { Slugs = allowedSlugs.ToList() }],
        Actions = [new RunAgentActionSpec { Agent = "{assignee}", ConcurrencyGroup = "{assignee}" }],
    };

    private static string DebugLog(Harness h) =>
        Path.Combine(h.Workspace, ".agents", "channel", "debug.log");

    private static string ReadDebugLog(Harness h) =>
        File.Exists(DebugLog(h)) ? File.ReadAllText(DebugLog(h)) : "";

    [Fact]
    public async Task TodoTicketAssignedToMemberWithoutDefinition_WritesSkippedLineAndActivity()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path, AssigneeDispatchAutomation("programmer"));
        await h.Members.CreateMemberAsync(h.Slug, "documentalist");
        var ticket = await h.Tickets.CreateTicketAsync(
            h.Slug, "Frozen ticket", status: "Todo", assignedTo: "documentalist");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var log = ReadDebugLog(h);
        Assert.Contains($"SKIPPED #{ticket.Id}: no agent definition for 'documentalist'", log);
        var updated = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Contains(updated!.Activities, a =>
            a.Author == "automation" && a.Text == h.Loc.Get("ActAgentDefinitionMissing", "documentalist"));
    }

    [Fact]
    public async Task RepeatedPollsWithinAnHour_AreThrottledToOneLineAndOneActivity()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path, AssigneeDispatchAutomation("programmer"));
        await h.Members.CreateMemberAsync(h.Slug, "documentalist");
        var ticket = await h.Tickets.CreateTicketAsync(
            h.Slug, "Frozen ticket", status: "Todo", assignedTo: "documentalist");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var skippedLines = ReadDebugLog(h).Split('\n')
            .Count(l => l.Contains($"SKIPPED #{ticket.Id}:"));
        Assert.Equal(1, skippedLines);
        var updated = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Single(updated!.Activities, a =>
            a.Text == h.Loc.Get("ActAgentDefinitionMissing", "documentalist"));
    }

    [Fact]
    public async Task AssigneeWithDefinition_ProducesNoWarning()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path, AssigneeDispatchAutomation("worker"));
        await h.Members.CreateMemberAsync(h.Slug, "worker");
        TestSkillBuilder.Create(h.Workspace, "worker", scenario: "unused");
        var ticket = await h.Tickets.CreateTicketAsync(
            h.Slug, "Healthy ticket", status: "Todo", assignedTo: "worker");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.DoesNotContain($"SKIPPED #{ticket.Id}:", ReadDebugLog(h));
    }

    [Fact]
    public async Task UnassignedTicket_ProducesNoWarning()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path, AssigneeDispatchAutomation("programmer"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Unassigned", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.DoesNotContain($"SKIPPED #{ticket.Id}:", ReadDebugLog(h));
    }

    [Fact]
    public async Task WarnerRewarns_AfterThrottleWindowElapses()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        await h.Members.CreateMemberAsync(h.Slug, "ghost");
        var ticket = await h.Tickets.CreateTicketAsync(
            h.Slug, "Frozen", status: "Todo", assignedTo: "ghost");
        var warner = new MissingAgentDefinitionWarner(
            h.Tickets, h.Loc, NullLogger.Instance);
        var rt = new ProjectRuntime(h.Slug) { Workspace = h.Workspace };
        var automation = AssigneeDispatchAutomation("programmer");
        var firing = new TriggerFiring(ticket.Id, ticket.Title, ticket.Status);
        var t0 = DateTime.UtcNow;

        await warner.ObserveColumnFiringAsync(rt, automation, firing, t0);
        await warner.ObserveColumnFiringAsync(rt, automation, firing, t0.AddMinutes(30));
        await warner.ObserveColumnFiringAsync(rt, automation, firing, t0 + MissingAgentDefinitionWarner.WarnInterval);

        var skippedLines = ReadDebugLog(h).Split('\n')
            .Count(l => l.Contains($"SKIPPED #{ticket.Id}:"));
        Assert.Equal(2, skippedLines);
    }

    [Fact]
    public async Task DefinitionAppearingLater_ClearsStateAndStopsWarning()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        await h.Members.CreateMemberAsync(h.Slug, "ghost");
        var ticket = await h.Tickets.CreateTicketAsync(
            h.Slug, "Frozen", status: "Todo", assignedTo: "ghost");
        var warner = new MissingAgentDefinitionWarner(
            h.Tickets, h.Loc, NullLogger.Instance);
        var rt = new ProjectRuntime(h.Slug) { Workspace = h.Workspace };
        var automation = AssigneeDispatchAutomation("programmer");
        var firing = new TriggerFiring(ticket.Id, ticket.Title, ticket.Status);
        var t0 = DateTime.UtcNow;

        await warner.ObserveColumnFiringAsync(rt, automation, firing, t0);
        TestSkillBuilder.Create(h.Workspace, "ghost", scenario: "unused");
        await warner.ObserveColumnFiringAsync(rt, automation, firing, t0 + MissingAgentDefinitionWarner.WarnInterval);

        var skippedLines = ReadDebugLog(h).Split('\n')
            .Count(l => l.Contains($"SKIPPED #{ticket.Id}:"));
        Assert.Equal(1, skippedLines);
    }

    [Fact]
    public void DispatchesAssigneeAgent_DetectsPlaceholderOnly()
    {
        Assert.True(MissingAgentDefinitionWarner.DispatchesAssigneeAgent(new AutomationRule
        {
            Id = "a",
            Trigger = new TicketInColumnTriggerSpec(),
            Actions = [new RunAgentActionSpec { Agent = "{assignee}" }],
        }));
        Assert.False(MissingAgentDefinitionWarner.DispatchesAssigneeAgent(new AutomationRule
        {
            Id = "b",
            Trigger = new TicketInColumnTriggerSpec(),
            Actions = [new RunAgentActionSpec { Agent = "groomer" }],
        }));
        Assert.False(MissingAgentDefinitionWarner.DispatchesAssigneeAgent(new AutomationRule
        {
            Id = "c",
            Trigger = new TicketInColumnTriggerSpec(),
            Actions = [new AddCommentActionSpec { Content = "x", Author = "automation" }],
        }));
    }
}
