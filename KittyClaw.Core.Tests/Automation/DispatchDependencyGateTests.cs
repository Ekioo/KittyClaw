using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Integration tests for ticket #197: the dispatch gate blocks tickets that have
/// unresolved blockers and posts a visible comment; once all blockers are Done the
/// gate passes on the next dispatch cycle.
/// </summary>
public sealed class DispatchDependencyGateTests
{
    private static async Task<(
        ActionExecutor executor,
        ProjectRuntime runtime,
        TicketService tickets,
        ColumnService columns,
        AgentRunRegistry runs,
        string slug)> BuildAsync(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("dep-gate-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 4);
        var runner = new AgentRunner(sessions, runs, gate, NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var appSettings = new AppSettingsService(tmp.Path);
        var loc = new LocalizationService(appSettings);
        var tickets = new TicketService(projects, members);
        var columns = new ColumnService(projects);
        var runState = new RunStateManager(runs, cost, tickets, NullLogger.Instance);

        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState,
            NullLogger.Instance);

        var rt = new ProjectRuntime(project.Slug);
        rt.Workspace = workspace;
        rt.Config = new AutomationConfig { Automations = [] };

        return (executor, rt, tickets, columns, runs, project.Slug);
    }

    private static AutomationRule MakeRunAgentAutomation() =>
        new()
        {
            Id = "test-dispatch",
            Enabled = true,
            Trigger = new TicketInColumnTriggerSpec { Columns = ["Todo"], Seconds = 30 },
            Conditions = [],
            Actions = [new RunAgentActionSpec { Agent = "programmer", MaxTurns = 1 }],
        };

    // ── Case 1: unresolved blocker prevents dispatch ──────────────────────────

    [Fact]
    public async Task Dispatch_skipped_when_blocker_is_not_Done()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, _, runs, slug) = await BuildAsync(tmp);

        var blocker = await tickets.CreateTicketAsync(slug, "Blocker ticket");
        var blocked = await tickets.CreateTicketAsync(slug, "Blocked ticket");
        await tickets.AddDependencyAsync(slug, blocked.Id, blocker.Id);

        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(blocked.Id, blocked.Title, blocked.Status);

        await executor.ExecuteAutomationToCompletionAsync(rt, automation, firing, CancellationToken.None);

        Assert.Empty(runs.AllForTicket(slug, blocked.Id));
    }

    // ── Case 2: comment posted, not duplicated on repeated cycles ─────────────

    [Fact]
    public async Task Blocker_comment_is_posted_once_and_not_duplicated()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, _, runs, slug) = await BuildAsync(tmp);

        var blocker = await tickets.CreateTicketAsync(slug, "Upstream work");
        var blocked = await tickets.CreateTicketAsync(slug, "Downstream ticket");
        await tickets.AddDependencyAsync(slug, blocked.Id, blocker.Id);

        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(blocked.Id, blocked.Title, blocked.Status);

        // Two consecutive dispatch attempts (simulates two 30-second engine cycles)
        await executor.ExecuteAutomationToCompletionAsync(rt, automation, firing, CancellationToken.None);
        await executor.ExecuteAutomationToCompletionAsync(rt, automation, firing, CancellationToken.None);

        var updated = await tickets.GetTicketAsync(slug, blocked.Id);
        var blockerComments = updated!.Comments
            .Where(c => c.Author == "automation" && c.Content.Contains("Dispatch blocked"))
            .ToList();

        Assert.Single(blockerComments);
        Assert.Contains($"#{blocker.Id}", blockerComments[0].Content);
        Assert.Contains("Upstream work", blockerComments[0].Content);
    }

    // ── Case 3: comment names the blocker's current status ───────────────────

    [Fact]
    public async Task Blocker_comment_includes_blocker_status()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, _, runs, slug) = await BuildAsync(tmp);

        var blocker = await tickets.CreateTicketAsync(slug, "Pending task");
        await tickets.MoveTicketAsync(slug, blocker.Id, "InProgress", "owner");
        var blocked = await tickets.CreateTicketAsync(slug, "Waiting task");
        await tickets.AddDependencyAsync(slug, blocked.Id, blocker.Id);

        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(blocked.Id, blocked.Title, blocked.Status);

        await executor.ExecuteAutomationToCompletionAsync(rt, automation, firing, CancellationToken.None);

        var updated = await tickets.GetTicketAsync(slug, blocked.Id);
        var comment = updated!.Comments.Single(c => c.Author == "automation");
        Assert.Contains("InProgress", comment.Content);
    }

    // ── Case 4: new comment posted when blocker set changes ──────────────────

    [Fact]
    public async Task New_comment_posted_when_different_blockers_are_unresolved()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, _, runs, slug) = await BuildAsync(tmp);

        var blockerA = await tickets.CreateTicketAsync(slug, "Blocker A");
        var blockerB = await tickets.CreateTicketAsync(slug, "Blocker B");
        var blocked = await tickets.CreateTicketAsync(slug, "Blocked ticket");
        await tickets.AddDependencyAsync(slug, blocked.Id, blockerA.Id);
        await tickets.AddDependencyAsync(slug, blocked.Id, blockerB.Id);

        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(blocked.Id, blocked.Title, blocked.Status);

        // First dispatch with both blockers → one comment mentioning both
        await executor.ExecuteAutomationToCompletionAsync(rt, automation, firing, CancellationToken.None);

        // Resolve A; now only B is unresolved → new comment with just B
        await tickets.MoveTicketAsync(slug, blockerA.Id, "Done", "owner");
        await executor.ExecuteAutomationToCompletionAsync(rt, automation, firing, CancellationToken.None);

        var updated = await tickets.GetTicketAsync(slug, blocked.Id);
        var blockerComments = updated!.Comments
            .Where(c => c.Author == "automation" && c.Content.Contains("Dispatch blocked"))
            .ToList();

        // Two comments: one for {A,B}, one for {B} alone
        Assert.Equal(2, blockerComments.Count);
        Assert.Contains($"#{blockerB.Id}", blockerComments[1].Content);
    }

    // ── Case 5: gate passes and no blocker comment when all blockers are Done ─
    // Uses ExecuteAutomationAsync (not ToCompletion) to avoid waiting for a background
    // agent run; the gate check completes before the run is detached.

    [Fact]
    public async Task No_blocker_comment_when_all_blockers_are_Done()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, _, runs, slug) = await BuildAsync(tmp);

        var blocker = await tickets.CreateTicketAsync(slug, "Resolved blocker");
        await tickets.MoveTicketAsync(slug, blocker.Id, "Done", "owner");
        var blocked = await tickets.CreateTicketAsync(slug, "Ready ticket");
        await tickets.AddDependencyAsync(slug, blocked.Id, blocker.Id);

        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(blocked.Id, blocked.Title, blocked.Status);

        // ExecuteAutomationAsync returns as soon as the run is detached; by that point
        // the dependency gate has already been evaluated (before the run starts).
        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None);

        await AssertEventuallyAsync(
            () => runs.AllForTicket(slug, blocked.Id).Any(),
            TimeSpan.FromSeconds(5));

        var updated = await tickets.GetTicketAsync(slug, blocked.Id);
        var blockerComments = updated!.Comments
            .Where(c => c.Author == "automation" && c.Content.Contains("Dispatch blocked"))
            .ToList();

        Assert.Empty(blockerComments);
        Assert.NotEmpty(runs.AllForTicket(slug, blocked.Id));
    }

    [Fact]
    public async Task Dependency_in_project_specific_success_column_is_resolved()
    {
        using var tmp = new TempDir();
        var (executor, rt, tickets, columns, runs, slug) = await BuildAsync(tmp);

        var success = (await columns.ListColumnsAsync(slug)).Single(c => c.Role == ColumnRole.Success);
        await columns.UpdateColumnAsync(slug, success.Id, name: "Livré");

        var blocker = await tickets.CreateTicketAsync(slug, "Localized resolved blocker");
        await tickets.MoveTicketAsync(slug, blocker.Id, "Livré", "owner");
        var blocked = await tickets.CreateTicketAsync(slug, "Ready downstream ticket");
        await tickets.AddDependencyAsync(slug, blocked.Id, blocker.Id);

        var automation = MakeRunAgentAutomation();
        var firing = new TriggerFiring(blocked.Id, blocked.Title, blocked.Status);
        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None);

        await AssertEventuallyAsync(
            () => runs.AllForTicket(slug, blocked.Id).Any(),
            TimeSpan.FromSeconds(5));

        var updated = await tickets.GetTicketAsync(slug, blocked.Id);
        Assert.Equal(ColumnRole.Success, Assert.Single(updated!.BlockedBy).ColumnRole);
        Assert.DoesNotContain(updated.Comments,
            c => c.Author == "automation" && c.Content.Contains("Dispatch blocked"));
    }

    private static async Task AssertEventuallyAsync(Func<bool> assertion, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!assertion() && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(assertion(), "Expected dispatch run was not registered before the timeout.");
    }
}
