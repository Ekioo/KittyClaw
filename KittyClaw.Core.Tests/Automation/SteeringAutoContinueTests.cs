using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

// Tests for ticket #126 (second cycle):
//   - AgentRunSnapshot must survive PendingSteerMessages across save/load.
//   - When a chat run ends with PendingSteerMessages, an auto-continue turn must
//     start automatically (no explicit user action required).
//   - A stopped/cancelled run must NOT trigger an auto-continue.

[Collection("MockClaude")]
public class SteeringAutoContinueTests
{
    // ── Test 1 ───────────────────────────────────────────────────────────────
    // AgentRunSnapshot does not currently include a PendingSteerMessages field,
    // so pending messages are silently lost on server restart.
    //
    // Currently FAILS (runtime): RunLogStore.Save() does not copy PendingSteerMessages
    // into the snapshot, so LoadAll() returns runs with an empty list.
    [Fact]
    public async Task AgentRunSnapshot_RoundTrips_PendingSteerMessages()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);

        var run = new AgentRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            ProjectSlug = "snap-test",
            TicketId = null,
            AgentName = "snap-agent",
            SkillFile = "(inline)",
            ConcurrencyGroup = "chat:snap-test:snap-agent",
            StartedAt = DateTime.UtcNow,
        };
        run.AddPendingSteerMessage("pending-alpha");
        run.AddPendingSteerMessage("pending-beta");
        run.Status = AgentRunStatus.Completed;
        run.EndedAt = DateTime.UtcNow;

        store.Save(run);

        var loaded = store.LoadAll().FirstOrDefault(r => r.RunId == run.RunId);
        Assert.NotNull(loaded);

        // Currently fails: PendingSteerMessages not persisted in AgentRunSnapshot.
        Assert.Equal(2, loaded.PendingSteerMessages.Count);
        Assert.Contains("pending-alpha", loaded.PendingSteerMessages);
        Assert.Contains("pending-beta", loaded.PendingSteerMessages);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────
    // A steer message that arrives while stdin is closed (--print mode) must not be
    // lost: ClaudeRunner auto-replays it as a follow-up turn. Since ticket #126 the
    // replay happens INSIDE the same registered run (steer_replay + WithChatReplay),
    // not as a second AgentRunRegistry entry — assert the replay fired and nothing
    // stayed pending.
    [Fact]
    public async Task ChatRun_WithPendingSteerMessages_AutoContinues()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("auto-continue-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new ClaudeRunner(
            sessions, runs, new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);

        AgentRun? activeRun = null;
        runs.OnRunStarted += r => activeRun = r;

        var concurrencyGroup = $"chat:{project.Slug}:steer-agent";
        var steered = 0;
        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "steer-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "# steer-agent\n\n<!--scenario:default-->",
            ExtraContext = "hello",
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = concurrencyGroup,
            ChatTarget = "steer-agent",
            OnEventHook = ev =>
            {
                // Queue a steer message on launch; stdin is already closed at this
                // point so PumpSteeringAsync will add it to PendingSteerMessages.
                // Once only: the hook is inherited by the auto-continue follow-up run,
                // and re-steering every launch would auto-continue forever (each run ends
                // with a fresh undelivered steer), leaving subprocesses spawning after the
                // test ends — this is what made the whole suite hang.
                if (ev.Kind == "launch" && activeRun is not null
                    && Interlocked.CompareExchange(ref steered, 1, 0) == 0)
                {
                    activeRun.SteeringQueue.Writer.TryWrite("steer-while-thinking");
                }
            },
        };

        var run = await runner.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "steer_replay");
        Assert.Empty(run.PendingSteerMessages);
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────
    // A run that is cancelled/stopped must NOT trigger an auto-continue even if
    // it has pending steer messages. Guards against spurious extra runs.
    //
    // Currently passes as a regression guard; must continue to pass after
    // the auto-continue mechanism is added.
    [Fact]
    public async Task AutoContinue_StoppedRun_DoesNotFire()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("no-autocontinue-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new ClaudeRunner(
            sessions, runs, new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);

        AgentRun? activeRun = null;
        runs.OnRunStarted += r => activeRun = r;

        var concurrencyGroup = $"chat:{project.Slug}:steer-agent";
        using var cts = new CancellationTokenSource();

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "steer-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "# steer-agent\n\n<!--scenario:default-->",
            ExtraContext = "hello",
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = concurrencyGroup,
            ChatTarget = "steer-agent",
            OnEventHook = ev =>
            {
                if (ev.Kind == "launch" && activeRun is not null)
                {
                    activeRun.SteeringQueue.Writer.TryWrite("steer-on-stopped-run");
                    cts.Cancel();
                }
            },
        };

        await runner.RunAsync(ctx, cts.Token);

        // Short wait to ensure no auto-continue fires for a stopped run.
        await Task.Delay(200);

        var runsInGroup = runs.AllForProject(project.Slug)
            .Where(r => r.ConcurrencyGroup == concurrencyGroup)
            .ToList();
        Assert.Single(runsInGroup);
    }
}
