using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// §2.4 (backport analysis): the statusChange ticket snapshot used to be SHARED by every
/// automation of a project. One workflow committing its firing acknowledged the transition
/// for ALL of them — a second workflow that had been skipped (concurrency gate, budget)
/// lost its retry and silently never fired. Snapshots are now isolated per automation,
/// with the legacy shared snapshot kept as a fresh migration seed.
/// </summary>
public class SnapshotIsolationTests
{
    private sealed record Harness(
        SessionRegistry Sessions, TicketService Tickets, string Slug, string Workspace, int TicketId, string InitialStatus);

    private static async Task<Harness> BuildAsync(string dataDir)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("snapshot-isolation-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Raced ticket", "", "owner");

        return new Harness(new SessionRegistry(), tickets, project.Slug, workspace, ticket.Id, ticket.Status);
    }

    private static (StatusChangeTrigger Trigger, AutomationRule Automation) MakeAutomation(string id, string to) =>
        (new StatusChangeTrigger(new StatusChangeTriggerSpec { To = to, PollSeconds = 0 }),
         new AutomationRule { Id = id, Enabled = true, Trigger = new StatusChangeTriggerSpec { To = to, PollSeconds = 0 } });

    private static TriggerContext Ctx(Harness h, AutomationRule automation) => new()
    {
        ProjectSlug = h.Slug,
        WorkspacePath = h.Workspace,
        Automation = automation,
        Tickets = h.Tickets,
        Members = null!,
        Sessions = h.Sessions,
        Runs = new AgentRunRegistry(),
        Now = DateTime.UtcNow,
    };

    // ── The §2.4 regression scenario ─────────────────────────────────────────

    [Fact]
    public async Task CommitByOneAutomation_DoesNotAcknowledgeAnotherAutomationsRetry()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var (trigA, autoA) = MakeAutomation("auto-a", "Done");
        var (trigB, autoB) = MakeAutomation("auto-b", "Done");

        // Both automations have polled at least once before the transition (normal life:
        // every trigger evaluates every engine tick), so each owns a snapshot.
        Assert.Empty(await trigA.EvaluateAsync(Ctx(h, autoA), CancellationToken.None));
        Assert.Empty(await trigB.EvaluateAsync(Ctx(h, autoB), CancellationToken.None));

        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Done", "test");

        // Both see the transition. A's chain runs and commits; B's chain was skipped
        // (concurrency gate, budget...) and must retry later.
        var firingA = Assert.Single(await trigA.EvaluateAsync(Ctx(h, autoA), CancellationToken.None));
        Assert.Single(await trigB.EvaluateAsync(Ctx(h, autoB), CancellationToken.None));
        await trigA.CommitFiringAsync(Ctx(h, autoA), firingA);

        // THE bug: with the shared snapshot, A's commit acknowledged the transition for B
        // too, and this retry came back empty — B silently never processed the ticket.
        var retryB = await trigB.EvaluateAsync(Ctx(h, autoB), CancellationToken.None);
        Assert.Single(retryB);

        // And once B commits its own firing, its retries stop.
        await trigB.CommitFiringAsync(Ctx(h, autoB), retryB[0]);
        Assert.Empty(await trigB.EvaluateAsync(Ctx(h, autoB), CancellationToken.None));

        // A's snapshot was never disturbed by B's commit either.
        Assert.Empty(await trigA.EvaluateAsync(Ctx(h, autoA), CancellationToken.None));
    }

    // ── Soft migration from the legacy shared snapshot ───────────────────────

    [Fact]
    public async Task AutomationWithoutOwnSnapshot_SeedsFromLegacy_NoReplayOfOldTransitions()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        // Pre-upgrade state: only the legacy shared snapshot exists, up to date.
        h.Sessions.SaveTicketSnapshot(h.Workspace, new Dictionary<int, string> { [h.TicketId] = h.InitialStatus });

        // First evaluation after the upgrade must NOT fire: the current status matches
        // the legacy seed, there is no transition to replay.
        var (trig, auto) = MakeAutomation("upgraded-auto", h.InitialStatus);
        Assert.Empty(await trig.EvaluateAsync(Ctx(h, auto), CancellationToken.None));

        // Real transitions after the seed still fire normally.
        var (trigDone, autoDone) = MakeAutomation("upgraded-auto-2", "Done");
        Assert.Empty(await trigDone.EvaluateAsync(Ctx(h, autoDone), CancellationToken.None));
        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Done", "test");
        Assert.Single(await trigDone.EvaluateAsync(Ctx(h, autoDone), CancellationToken.None));
    }

    [Fact]
    public async Task PartialLegacySnapshot_DoesNotReplayUnseenTicketsAlreadyInTargetStatus()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Done", "test");

        // A partial legacy snapshot can omit older tickets after concurrent status-change
        // automations wrote different snapshots. The omitted ticket has no observed previous
        // status, so its current Done state must be captured as baseline rather than replayed.
        h.Sessions.SaveTicketSnapshot(h.Workspace, new Dictionary<int, string>());

        var (trigger, automation) = MakeAutomation("committer-on-done", "Done");
        Assert.Empty(await trigger.EvaluateAsync(Ctx(h, automation), CancellationToken.None));
        Assert.Equal("Done", h.Sessions.TicketSnapshot(h.Workspace, automation.Id)[h.TicketId]);
    }

    [Fact]
    public async Task PerAutomationSaves_KeepTheLegacySnapshotFresh()
    {
        // The legacy shared snapshot is the seed for automations that don't have their own
        // yet (a new automation, or a rollback to an older KittyClaw). Per-automation
        // saves write through to it so it never freezes at upgrade time.
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        h.Sessions.SaveTicketSnapshot(h.Workspace, "some-automation",
            new Dictionary<int, string> { [h.TicketId] = "Done" });

        var legacy = h.Sessions.TicketSnapshot(h.Workspace);
        Assert.Equal("Done", legacy[h.TicketId]);
    }

    [Fact]
    public async Task ConsumedDoneTransition_DoesNotReplayAcrossPollsReloadOrMetadataChanges()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var (trigger, automation) = MakeAutomation("committer-on-done", "Done");
        Assert.Empty(await trigger.EvaluateAsync(Ctx(h, automation), CancellationToken.None));

        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Done", "test");
        var firing = Assert.Single(await trigger.EvaluateAsync(Ctx(h, automation), CancellationToken.None));
        Assert.True(trigger.TryConsumeFiring(Ctx(h, automation), firing));

        for (var poll = 0; poll < 10; poll++)
            Assert.Empty(await trigger.EvaluateAsync(Ctx(h, automation), CancellationToken.None));

        await h.Tickets.AddCommentAsync(h.Slug, h.TicketId, "metadata-only update", "owner");
        var reloaded = new StatusChangeTrigger(new StatusChangeTriggerSpec { To = "Done", PollSeconds = 0 });
        Assert.Empty(await reloaded.EvaluateAsync(Ctx(h, automation), CancellationToken.None));
        Assert.False(reloaded.TryConsumeFiring(Ctx(h, automation), firing));
    }

    [Fact]
    public async Task ConsumedDoneTransition_DoesNotReplayAfterSessionRegistryRestart()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var (trigger, automation) = MakeAutomation("committer-on-done", "Done");
        Assert.Empty(await trigger.EvaluateAsync(Ctx(h, automation), CancellationToken.None));

        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Done", "test");
        var firing = Assert.Single(await trigger.EvaluateAsync(Ctx(h, automation), CancellationToken.None));
        Assert.True(trigger.TryConsumeFiring(Ctx(h, automation), firing));

        var restarted = h with { Sessions = new SessionRegistry() };
        var restartedTrigger = new StatusChangeTrigger(
            new StatusChangeTriggerSpec { To = "Done", PollSeconds = 0 });

        Assert.Empty(await restartedTrigger.EvaluateAsync(Ctx(restarted, automation), CancellationToken.None));
        Assert.False(restartedTrigger.TryConsumeFiring(Ctx(restarted, automation), firing));
    }

    [Fact]
    public async Task LeavingAndReenteringDone_CreatesOneNewOccurrenceForEachAutomation()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var (triggerA, automationA) = MakeAutomation("committer-on-done", "Done");
        var (triggerB, automationB) = MakeAutomation("evaluator-on-done", "Done");
        Assert.Empty(await triggerA.EvaluateAsync(Ctx(h, automationA), CancellationToken.None));
        Assert.Empty(await triggerB.EvaluateAsync(Ctx(h, automationB), CancellationToken.None));

        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Done", "test");
        var firstA = Assert.Single(await triggerA.EvaluateAsync(Ctx(h, automationA), CancellationToken.None));
        var firstB = Assert.Single(await triggerB.EvaluateAsync(Ctx(h, automationB), CancellationToken.None));
        Assert.True(triggerA.TryConsumeFiring(Ctx(h, automationA), firstA));
        Assert.True(triggerB.TryConsumeFiring(Ctx(h, automationB), firstB));

        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Review", "test");
        Assert.Empty(await triggerA.EvaluateAsync(Ctx(h, automationA), CancellationToken.None));
        Assert.Empty(await triggerB.EvaluateAsync(Ctx(h, automationB), CancellationToken.None));
        await h.Tickets.MoveTicketAsync(h.Slug, h.TicketId, "Done", "test");

        Assert.True(triggerA.TryConsumeFiring(Ctx(h, automationA),
            Assert.Single(await triggerA.EvaluateAsync(Ctx(h, automationA), CancellationToken.None))));
        Assert.True(triggerB.TryConsumeFiring(Ctx(h, automationB),
            Assert.Single(await triggerB.EvaluateAsync(Ctx(h, automationB), CancellationToken.None))));
    }

    [Fact]
    public async Task ConcurrentDuplicateConsumers_OnlyOneWins()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var (_, automation) = MakeAutomation("committer-on-done", "Done");
        var firing = new TriggerFiring(h.TicketId, "Raced ticket", "Done");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            h.Sessions.TryConsumeStatusTransition(h.Workspace, automation.Id, h.TicketId, firing.TicketStatus!))));

        Assert.Single(attempts, value => value);
    }

    [Fact]
    public async Task SignalPath_LeaveAndReenterDoneBeforePoll_CreatesNewOccurrence()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var (trigger, automation) = MakeAutomation("committer-on-done", "Done");
        var ctx = Ctx(h, automation);

        Assert.True(trigger.TryHandleExternalSignal(
            new StatusChangeSignal(h.TicketId, "Review", "Done"), out var firstFirings));
        Assert.True(trigger.TryConsumeFiring(ctx, Assert.Single(firstFirings)));

        Assert.True(trigger.TryHandleExternalSignal(
            new StatusChangeSignal(h.TicketId, "Done", "Review"), out var leaveFirings));
        var leave = Assert.Single(leaveFirings);
        Assert.False(leave.ShouldDispatch);
        Assert.True(trigger.TryConsumeFiring(ctx, leave));

        Assert.True(trigger.TryHandleExternalSignal(
            new StatusChangeSignal(h.TicketId, "Review", "Done"), out var secondFirings));
        Assert.True(trigger.TryConsumeFiring(ctx, Assert.Single(secondFirings)));
    }
}
