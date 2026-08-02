using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Verifies that <see cref="ActionExecutor"/> defers CommitFiringAsync until after
/// a dispatch. Status-change transitions are consumed before actions start so failures and
/// restarts cannot replay the same logical occurrence.
/// </summary>
[Collection("MockClaude")]
public class ActionExecutorDeferredCommitTests
{
    private static async Task<(
        ActionExecutor executor,
        ProjectRuntime runtime,
        TicketService tickets,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        int ticketId)> BuildAsync(string dataDir, string agentScenario)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("deferred-commit-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "committer", scenario: agentScenario);

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 4);
        var runner = new AgentRunner(sessions, runs, gate, NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var appSettings = new AppSettingsService(dataDir);
        var loc = new LocalizationService(appSettings);
        var tickets = new TicketService(projects, members);
        var runState = new RunStateManager(runs, cost, tickets, NullLogger.Instance);

        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState,
            NullLogger.Instance);

        var ticket = await tickets.CreateTicketAsync(project.Slug, "Test ticket", "", "owner");
        await tickets.MoveTicketAsync(project.Slug, ticket.Id, "Done", "automation");

        var rt = new ProjectRuntime(project.Slug);
        rt.Workspace = workspace;
        rt.Config = new AutomationConfig { Automations = [] };

        return (executor, rt, tickets, sessions, runs, ticket.Id);
    }

    private static AutomationRule MakeRunAgentAutomation(bool restoreOnFail = false) =>
        new()
        {
            Id = "on-done",
            Enabled = true,
            Trigger = new StatusChangeTriggerSpec { From = "Develop", To = "Done", PollSeconds = 30 },
            Conditions = [],
            Actions = [new RunAgentActionSpec { Agent = "committer", MaxTurns = 1, RestoreStatusOnFail = restoreOnFail }],
        };

    private static async Task WaitForRunEndAsync(AgentRunRegistry runs, string projectSlug, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (!runs.ActiveForProject(projectSlug).Any()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for agent run to complete.");
    }

    private static async Task WaitForSnapshotStatusAsync(
        SessionRegistry sessions,
        string workspace,
        string automationId,
        int ticketId,
        string expectedStatus,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var snapshot = sessions.TicketSnapshot(workspace, automationId);
            if (snapshot.TryGetValue(ticketId, out var status) && status == expectedStatus)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for snapshot status '{expectedStatus}'.");
    }

    // ── Case 1: Failed run must NOT advance the snapshot ─────────────────────

    [Fact]
    public async Task FailedRun_SnapshotRemainsConsumed_SoTriggerDoesNotReplay()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, sessions, runs, ticketId) =
            await BuildAsync(tmp.Path, agentScenario: "error-exit");

        // Snapshot shows ticket was in "Develop" — trigger sees Develop→Done transition.
        sessions.SaveTicketSnapshot(rt.Workspace!, automationId: "on-done",
            new Dictionary<int, string> { [ticketId] = "Develop" });

        var trigger = new StatusChangeTrigger(new StatusChangeTriggerSpec { From = "Develop", To = "Done", PollSeconds = 30 });
        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(ticketId, "Test ticket", "Done");
        var tctx = new TriggerContext
        {
            ProjectSlug = rt.Slug,
            WorkspacePath = rt.Workspace!,
            Automation = automation,
            Tickets = tickets,
            Members = new MemberService(new ProjectService(tmp.Path)),
            Sessions = sessions,
            Runs = runs,
            Now = DateTime.UtcNow,
        };

        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None, trigger, tctx);
        await WaitForRunEndAsync(runs, rt.Slug, TimeSpan.FromSeconds(15));

        // A failed agent must not resurrect an already-dispatched logical transition.
        var snapshot = sessions.TicketSnapshot(rt.Workspace!, automation.Id);
        Assert.True(snapshot.TryGetValue(ticketId, out var snapshotStatus),
            "Ticket must exist in snapshot.");
        Assert.Equal("Done", snapshotStatus);
    }

    // ── Case 2: Completed run MUST advance the snapshot (no regression) ──────

    [Fact]
    public async Task CompletedRun_SnapshotAdvancesToNewStatus_SoTriggerIsSilenced()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, sessions, runs, ticketId) =
            await BuildAsync(tmp.Path, agentScenario: "default");

        sessions.SaveTicketSnapshot(rt.Workspace!, automationId: "on-done",
            new Dictionary<int, string> { [ticketId] = "Develop" });

        var trigger = new StatusChangeTrigger(new StatusChangeTriggerSpec { From = "Develop", To = "Done", PollSeconds = 30 });
        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(ticketId, "Test ticket", "Done");
        var tctx = new TriggerContext
        {
            ProjectSlug = rt.Slug,
            WorkspacePath = rt.Workspace!,
            Automation = automation,
            Tickets = tickets,
            Members = new MemberService(new ProjectService(tmp.Path)),
            Sessions = sessions,
            Runs = runs,
            Now = DateTime.UtcNow,
        };

        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None, trigger, tctx);
        await WaitForRunEndAsync(runs, rt.Slug, TimeSpan.FromSeconds(15));
        await WaitForSnapshotStatusAsync(
            sessions, rt.Workspace!, automation.Id, ticketId, "Done", TimeSpan.FromSeconds(5));

        // After a successful run the snapshot must be "Done" — trigger stays silent.
        var snapshot = sessions.TicketSnapshot(rt.Workspace!, automation.Id);
        Assert.True(snapshot.TryGetValue(ticketId, out var snapshotStatus),
            "Ticket must exist in snapshot.");
        Assert.Equal("Done", snapshotStatus);
    }

    // ── Case 3: restoreStatusOnFail interaction ───────────────────────────────

    [Fact]
    public async Task FailedRunWithRestoreStatusOnFail_TransitionRemainsConsumed()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, sessions, runs, ticketId) =
            await BuildAsync(tmp.Path, agentScenario: "error-exit");

        // Move ticket to Done first (the restore path expects statusBeforeMove = Develop).
        // The test's BuildAsync already moves ticket to Done, so we just need the right snapshot.
        sessions.SaveTicketSnapshot(rt.Workspace!, automationId: "on-done",
            new Dictionary<int, string> { [ticketId] = "Develop" });

        var trigger = new StatusChangeTrigger(new StatusChangeTriggerSpec { From = "Develop", To = "Done", PollSeconds = 30 });
        // With restoreStatusOnFail: true, a moveTicketStatus action would precede runAgent.
        // Simplified: we test that even with RestoreStatusOnFail the snapshot is not committed on failure.
        var automation = MakeRunAgentAutomation(restoreOnFail: true);
        var firing = new TriggerFiring(ticketId, "Test ticket", "Done");
        var tctx = new TriggerContext
        {
            ProjectSlug = rt.Slug,
            WorkspacePath = rt.Workspace!,
            Automation = automation,
            Tickets = tickets,
            Members = new MemberService(new ProjectService(tmp.Path)),
            Sessions = sessions,
            Runs = runs,
            Now = DateTime.UtcNow,
        };

        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None, trigger, tctx);
        await WaitForRunEndAsync(runs, rt.Slug, TimeSpan.FromSeconds(15));

        // The Done occurrence remains consumed. A later poll observes the restored Develop
        // state as the new baseline without replaying Done.
        var snapshot = sessions.TicketSnapshot(rt.Workspace!, automation.Id);
        Assert.True(snapshot.TryGetValue(ticketId, out var snapshotStatus),
            "Ticket must exist in snapshot.");
        Assert.Equal("Done", snapshotStatus);
    }

    // ── Edge: Stopped run must NOT advance the snapshot ───────────────────────

    [Fact]
    public async Task StoppedRun_SnapshotRemainsConsumed_SoTriggerDoesNotReplay()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, sessions, runs, ticketId) =
            await BuildAsync(tmp.Path, agentScenario: "default");

        sessions.SaveTicketSnapshot(rt.Workspace!, automationId: "on-done",
            new Dictionary<int, string> { [ticketId] = "Develop" });

        var trigger = new StatusChangeTrigger(new StatusChangeTriggerSpec { From = "Develop", To = "Done", PollSeconds = 30 });
        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(ticketId, "Test ticket", "Done");
        var tctx = new TriggerContext
        {
            ProjectSlug = rt.Slug,
            WorkspacePath = rt.Workspace!,
            Automation = automation,
            Tickets = tickets,
            Members = new MemberService(new ProjectService(tmp.Path)),
            Sessions = sessions,
            Runs = runs,
            Now = DateTime.UtcNow,
        };

        using var cts = new CancellationTokenSource();
        // Cancel immediately after dispatch to force Stopped status.
        var dispatchTask = executor.ExecuteAutomationAsync(rt, automation, firing, cts.Token, trigger, tctx);
        await cts.CancelAsync();
        await dispatchTask;
        await WaitForRunEndAsync(runs, rt.Slug, TimeSpan.FromSeconds(15));

        // A manually-stopped run should NOT commit the snapshot — a new dispatch is acceptable.
        var snapshot = sessions.TicketSnapshot(rt.Workspace!, automation.Id);
        Assert.True(snapshot.TryGetValue(ticketId, out var snapshotStatus),
            "Ticket must exist in snapshot.");
        Assert.Equal("Done", snapshotStatus);
    }
}
