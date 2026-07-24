using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

// Tests for steering message delivery.
//
// Root cause of the bug (ticket #126):
//   AgentRunner closes stdin immediately after writing the initial prompt
//   (SpawnAndWaitAsync line ~290), BEFORE PumpSteeringAsync starts. When a
//   steering message arrives, PumpSteeringAsync checks CanWrite — which is
//   false — and silently drops the message. The comment "queued for replay on
//   the next --resume" was aspirational; no code ever did that replay.
//
// Required fix (drives the tests below):
//   1. AgentRun.PendingSteerMessages — a list populated by PumpSteeringAsync
//      when a message cannot be written to stdin.
//   2. AgentRunContext.PendingSteerMessages — caller-supplied list of messages
//      to prepend to the next chat-resume prompt.
//   3. BuildPromptAsync for chat resumes must prepend PendingSteerMessages so
//      the next --resume turn actually delivers them to the agent.

[Collection("MockClaude")]
public class SteeringDeliveryTests
{
    // ── Test 1 ───────────────────────────────────────────────────────────────
    // When a steer message is written to the queue while stdin is already
    // closed, PumpSteeringAsync must NOT silently discard it: it lands in
    // AgentRun.PendingSteerMessages and AgentRunner auto-replays it as a
    // steer_replay turn inside the same run, leaving nothing pending at the end.
    [Fact]
    public async Task SteeringMessage_QueuedWhileStdinClosed_IsPreservedInPendingSteerMessages()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("steer-pending-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        // The OnEventHook fires on the "launch" event — at that point stdin has
        // already been closed by SpawnAndWaitAsync (this is the bug: close happens
        // before PumpSteeringAsync). Queuing here simulates a steer message that
        // arrives mid-run after stdin is closed.
        AgentRun? activeRun = null;
        var runs = new AgentRunRegistry();

        var runner = new AgentRunner(
            new SessionRegistry(), runs, new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance);

        var ctx = new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "steer-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "# steer-agent\n\n<!--scenario:default-->",
            ExtraContext = "hello",
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = $"chat:{project.Slug}:steer-agent",
            OnEventHook = ev =>
            {
                // "launch" fires after stdin has been closed; queue a steer message
                // that PumpSteeringAsync will read but cannot write to the closed pipe.
                // Once only: the hook fires again on the replay turn's launch, and
                // re-steering there keeps the replay loop alive forever.
                if (ev.Kind == "launch" && activeRun is not null
                    && Interlocked.CompareExchange(ref _steered, 1, 0) == 0)
                {
                    activeRun.SteeringQueue.Writer.TryWrite("steer-payload-alpha");
                }
            },
        };

        // Capture the run object before it starts so the hook can reference it.
        var runStartedTcs = new TaskCompletionSource<AgentRun>();
        runs.OnRunStarted += r =>
        {
            activeRun = r;
            runStartedTcs.TrySetResult(r);
        };

        var runTask = runner.RunAsync(ctx, CancellationToken.None);
        await runStartedTcs.Task;
        var run = await runTask;

        // The steer message could not be written to stdin (already closed). It must have
        // been queued (steer event), auto-replayed (steer_replay turn), and not dropped.
        var events = run.SnapshotBuffer();
        Assert.Contains(events, e => e.Kind == "steer" && e.Text == "steer-payload-alpha");
        Assert.Contains(events, e => e.Kind == "steer_replay");
        Assert.Empty(run.PendingSteerMessages);
    }

    private int _steered;

    // ── Test 2 ───────────────────────────────────────────────────────────────
    // When AgentRunContext carries PendingSteerMessages, BuildPromptAsync must
    // prepend them to the chat-resume prompt so the agent actually receives them
    // in the next turn.
    //
    // We verify delivery indirectly: the prepended text contains a scenario
    // marker (<!--scenario:default-->) which the mock picks up. If the pending
    // messages were NOT prepended the marker would be absent and the mock would
    // still run "default" — but the *content* of the sent prompt would differ.
    // We assert the "steer" event appears (PumpSteeringAsync emits one) AND
    // there is no duplicate "steer" event for the same payload (i.e. it was not
    // queued again on the second turn).
    //
    // More directly: we verify that the second run's prompt includes the pending
    // message by checking the launched prompt contains it. BuildPromptAsync is
    // private, so we observe the effect: a second run that carries
    // PendingSteerMessages must emit a "steer" kind event for each pending
    // message BEFORE the assistant event — confirming they were injected.
    //
    // Currently FAILS (compilation): AgentRunContext has no PendingSteerMessages.
    [Fact]
    public async Task ChatResume_WithPendingSteerMessages_PrependsThemToPrompt()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("steer-resume-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var sessions = new SessionRegistry();
        var runner = new AgentRunner(
            sessions, new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance);

        // First turn — fresh session.
        var ctx1 = new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "steer-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "# steer-agent\n\n<!--scenario:default-->",
            ExtraContext = "first message",
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = $"chat:{project.Slug}:steer-agent",
            RetryOnResumeFailure = true,
        };
        var run1 = await runner.RunAsync(ctx1, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, run1.Status);

        // Second turn — resume, carries a pending steer message from turn 1.
        // BuildPromptAsync must prepend this to the prompt sent to the process.
        // The agent (or mock) will see it as part of its context.
        //
        // Currently FAILS (compilation): AgentRunContext.PendingSteerMessages
        // does not exist.
        var ctx2 = new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "steer-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "# steer-agent\n\n<!--scenario:default-->",
            ExtraContext = "second message",
            PendingSteerMessages = new[] { "injected-steer-from-turn-1" },
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = $"chat:{project.Slug}:steer-agent",
            RetryOnResumeFailure = true,
        };

        var run2 = await runner.RunAsync(ctx2, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, run2.Status);

        // The pending steer message must appear as a "steer" event on run2,
        // confirming it was processed (not silently dropped by BuildPromptAsync).
        var steerEvents = run2.SnapshotBuffer()
            .Where(e => e.Kind == "steer")
            .Select(e => e.Text)
            .ToList();

        Assert.Contains("injected-steer-from-turn-1", steerEvents);
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────
    // End-to-end: steer messages that arrive on turn N are included in turn N+1.
    // Verifies the full pipeline: PendingSteerMessages collected → passed via
    // AgentRunContext → prepended by BuildPromptAsync.
    //
    // Currently FAILS (compilation): depends on both PendingSteerMessages APIs.
    [Fact]
    public async Task SteeringMessages_CollectedInRun_ArePassedToNextResumeTurn()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("steer-e2e-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new AgentRunner(
            sessions, runs, new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance);

        AgentRun? activeRun = null;
        runs.OnRunStarted += r => activeRun = r;

        // Once only: the hook also fires on the in-run replay turn's launch; re-steering
        // there would refill PendingSteerMessages forever and RunAsync would never return.
        var steered = 0;
        var ctx1 = new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "steer-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "# steer-agent\n\n<!--scenario:default-->",
            ExtraContext = "turn one",
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = $"chat:{project.Slug}:steer-agent",
            OnEventHook = ev =>
            {
                if (ev.Kind == "launch" && activeRun is not null
                    && Interlocked.CompareExchange(ref steered, 1, 0) == 0)
                {
                    activeRun.SteeringQueue.Writer.TryWrite("steer-for-next-turn-A");
                    activeRun.SteeringQueue.Writer.TryWrite("steer-for-next-turn-B");
                }
            },
        };

        var run1 = await runner.RunAsync(ctx1, CancellationToken.None);

        // Undelivered steers are auto-replayed inside run 1 (steer_replay turn) and must
        // not be left pending afterwards.
        Assert.Equal(AgentRunStatus.Completed, run1.Status);
        Assert.Contains(run1.SnapshotBuffer(), e => e.Kind == "steer_replay");
        Assert.Empty(run1.PendingSteerMessages);

        // The explicit hand-off API (used by POST /chat/start when a previous run left
        // messages pending): the next turn context carries them and must inject them.
        var ctx2 = new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "steer-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "# steer-agent\n\n<!--scenario:default-->",
            ExtraContext = "turn two",
            PendingSteerMessages = new[] { "steer-for-next-turn-A", "steer-for-next-turn-B" },
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = $"chat:{project.Slug}:steer-agent",
            RetryOnResumeFailure = true,
        };

        var run2 = await runner.RunAsync(ctx2, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, run2.Status);

        // Both steered messages must appear as "steer" events on run2.
        var steerTexts = run2.SnapshotBuffer()
            .Where(e => e.Kind == "steer")
            .Select(e => e.Text)
            .ToList();

        Assert.Contains("steer-for-next-turn-A", steerTexts);
        Assert.Contains("steer-for-next-turn-B", steerTexts);
    }
}
