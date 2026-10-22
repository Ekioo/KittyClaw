using KittyClaw.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Tests for the dead man's switch (ticket #98, feature #3): the reaper force-releases a
/// concurrency lock held by a run that has been idle past its <see cref="AgentRun.LockTimeoutMinutes"/>.
/// </summary>
public class ConcurrencyLockReaperTests
{
    private static ConcurrencyLockReaper MakeReaper(AgentRunRegistry runs) =>
        new(runs, NullLogger<ConcurrencyLockReaper>.Instance);

    private static AgentRun MakeRun(int? lockTimeoutMinutes, string concurrencyGroup = "imap-poller") => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        ProjectSlug = "p",
        TicketId = null,
        AgentName = "poller",
        SkillFile = "poller/SKILL.md",
        ConcurrencyGroup = concurrencyGroup,
        StartedAt = DateTime.UtcNow,
        LockTimeoutMinutes = lockTimeoutMinutes,
    };

    [Fact]
    public void Reap_IdlePastTimeout_ForceStopsRun_AndReleasesLock()
    {
        var runs = new AgentRunRegistry();
        var run = MakeRun(lockTimeoutMinutes: 10);
        runs.Register(run);
        // Last heartbeat 20 min ago — well past the 10 min timeout.
        run.Push(new StreamEvent(DateTime.UtcNow.AddMinutes(-20), "assistant", "still thinking"));

        var reaped = MakeReaper(runs).Reap(DateTime.UtcNow);

        Assert.Single(reaped);
        Assert.Equal(AgentRunStatus.Stopped, run.Status);
        Assert.NotNull(run.EndedAt);
        // Lock is released: the group no longer has an active run.
        Assert.False(runs.HasActiveInGroup("p", "imap-poller"));
        // The subprocess (if any) is signalled to die.
        Assert.True(run.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void Reap_WithinTimeout_LeavesRunRunning()
    {
        var runs = new AgentRunRegistry();
        var run = MakeRun(lockTimeoutMinutes: 10);
        runs.Register(run);
        run.Push(new StreamEvent(DateTime.UtcNow.AddMinutes(-3), "assistant", "recent activity"));

        var reaped = MakeReaper(runs).Reap(DateTime.UtcNow);

        Assert.Empty(reaped);
        Assert.Equal(AgentRunStatus.Running, run.Status);
        Assert.True(runs.HasActiveInGroup("p", "imap-poller"));
    }

    [Fact]
    public void Reap_NoLockTimeoutConfigured_NeverReaps_EvenWhenLongIdle()
    {
        var runs = new AgentRunRegistry();
        var run = MakeRun(lockTimeoutMinutes: null);
        runs.Register(run);
        run.Push(new StreamEvent(DateTime.UtcNow.AddHours(-5), "assistant", "very stale but opted out"));

        var reaped = MakeReaper(runs).Reap(DateTime.UtcNow);

        Assert.Empty(reaped);
        Assert.Equal(AgentRunStatus.Running, run.Status);
    }

    [Fact]
    public void Reap_InteractiveChatWithoutExplicitTimeout_ReapsAfterDefaultTimeout()
    {
        var runs = new AgentRunRegistry();
        var run = MakeRun(lockTimeoutMinutes: null, concurrencyGroup: "chat:p:lain");
        runs.Register(run);
        run.Push(new StreamEvent(DateTime.UtcNow.AddMinutes(-31), "assistant", "stale chat"));

        var reaped = MakeReaper(runs).Reap(DateTime.UtcNow);

        Assert.Single(reaped);
        Assert.Equal(AgentRunStatus.Stopped, run.Status);
        Assert.False(runs.HasActiveInGroup("p", "chat:p:lain"));
    }

    [Fact]
    public void Reap_InteractiveChatWithoutExplicitTimeout_KeepsRecentRun()
    {
        var runs = new AgentRunRegistry();
        var run = MakeRun(lockTimeoutMinutes: null, concurrencyGroup: "chat:p:lain");
        runs.Register(run);
        run.Push(new StreamEvent(DateTime.UtcNow.AddMinutes(-5), "assistant", "recent activity"));

        var reaped = MakeReaper(runs).Reap(DateTime.UtcNow);

        Assert.Empty(reaped);
        Assert.Equal(AgentRunStatus.Running, run.Status);
    }

    [Fact]
    public void Reap_AlreadyCompletedRun_IsIgnored()
    {
        var runs = new AgentRunRegistry();
        var run = MakeRun(lockTimeoutMinutes: 1);
        runs.Register(run);
        run.Push(new StreamEvent(DateTime.UtcNow.AddHours(-1), "assistant", "old"));
        runs.Complete(run.RunId, AgentRunStatus.Completed, 0);

        var reaped = MakeReaper(runs).Reap(DateTime.UtcNow);

        Assert.Empty(reaped);
        // The reaper must not downgrade a terminal status.
        Assert.Equal(AgentRunStatus.Completed, run.Status);
    }

    [Fact]
    public void Reap_ThrowingEventSubscriber_DoesNotAbortReaping()
    {
        var runs = new AgentRunRegistry();
        var run = MakeRun(lockTimeoutMinutes: 5);
        runs.Register(run);
        run.Push(new StreamEvent(DateTime.UtcNow.AddMinutes(-30), "assistant", "old"));
        // A subscriber that throws on the reaper's own error event must not prevent lock release.
        run.OnEvent += _ => throw new InvalidOperationException("subscriber throws");

        var reaped = MakeReaper(runs).Reap(DateTime.UtcNow);

        Assert.Single(reaped);
        Assert.Equal(AgentRunStatus.Stopped, run.Status);
    }
}
