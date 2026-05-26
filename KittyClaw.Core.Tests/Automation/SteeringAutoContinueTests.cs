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
    // When a chat run completes with pending steer messages the system must
    // automatically start a second run in the same ConcurrencyGroup WITHOUT any
    // explicit user call to chat/start.
    //
    // Currently FAILS (runtime): no auto-continue mechanism exists; only one run
    // is ever registered in AgentRunRegistry.
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
                if (ev.Kind == "launch" && activeRun is not null)
                    activeRun.SteeringQueue.Writer.TryWrite("steer-while-thinking");
            },
        };

        await runner.RunAsync(ctx, CancellationToken.None);

        // Give the auto-continue task time to register the second run.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var count = runs.AllForProject(project.Slug)
                .Count(r => r.ConcurrencyGroup == concurrencyGroup);
            if (count >= 2) break;
            await Task.Delay(50);
        }

        // Currently fails: no auto-continue means only 1 run was ever started.
        var runsInGroup = runs.AllForProject(project.Slug)
            .Where(r => r.ConcurrencyGroup == concurrencyGroup)
            .ToList();
        Assert.True(runsInGroup.Count >= 2,
            $"Expected at least 2 runs (original + auto-continue) but found {runsInGroup.Count}.");
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
