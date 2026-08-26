using KittyClaw.Core.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Dead man's switch for concurrency-group locks (ticket #98, feature #3).
///
/// A RunAgent automation's concurrency group is "locked" for as long as a run in that group is
/// <see cref="AgentRunStatus.Running"/>. The nominal end paths and the <c>finally</c> safety net in
/// <see cref="AgentRunner"/> release the lock on any managed exit. But a pure hang — a claude
/// subprocess stuck on a network call that never returns and never throws — leaves the run Running
/// forever, so every later dispatch in that group is skipped silently until KittyClaw restarts.
///
/// This background service periodically scans all active runs and, for those whose automation set a
/// <see cref="AgentRun.LockTimeoutMinutes"/>, force-stops any run that has emitted no activity (no
/// StreamEvent) for longer than that timeout — cancelling it to kill the subprocess tree and
/// completing it as <see cref="AgentRunStatus.Stopped"/> so the lock is released immediately.
/// </summary>
public sealed class ConcurrencyLockReaper : BackgroundService
{
    internal const int DefaultInteractiveChatTimeoutMinutes = 30;
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<ConcurrencyLockReaper> _logger;

    // Reaping is coarse by design: LockTimeoutMinutes is expressed in whole minutes, so a 30s poll
    // detects a stalled lock within one poll of the deadline without adding noticeable overhead.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public ConcurrencyLockReaper(AgentRunRegistry runs, ILogger<ConcurrencyLockReaper> logger)
    {
        _runs = runs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConcurrencyLockReaper started (poll every {Seconds}s)", PollInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Reap(DateTime.UtcNow); }
            catch (Exception ex) { _logger.LogError(ex, "ConcurrencyLockReaper tick failed"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Force-releases every active run that opted into a lock timeout and has been idle past it.
    /// Returns the runs it reaped. <paramref name="now"/> is injected so tests are deterministic.
    /// </summary>
    internal IReadOnlyList<AgentRun> Reap(DateTime now)
    {
        var reaped = new List<AgentRun>();
        foreach (var run in _runs.AllActive())
        {
            var minutes = run.LockTimeoutMinutes;
            if (minutes is null && run.ConcurrencyGroup.StartsWith("chat:", StringComparison.OrdinalIgnoreCase))
                minutes = DefaultInteractiveChatTimeoutMinutes;
            if (minutes is not > 0) continue;
            var idle = now - run.LastActivityAt;
            if (idle <= TimeSpan.FromMinutes(minutes.Value)) continue;

            _logger.LogWarning(
                "ConcurrencyLockReaper: run={RunId} agent={Agent} group={Group} idle {Idle:F1} min > timeout {Timeout} min — force-releasing lock",
                run.RunId, run.AgentName, run.ConcurrencyGroup, idle.TotalMinutes, minutes.Value);

            // Emit a visible marker so the zombie is diagnosable in the run stream, then cancel to
            // kill the (possibly hung) subprocess tree, then complete to release the lock now.
            try
            {
                run.Push(new StreamEvent(now, "error",
                    $"Lock timeout: no activity for {idle.TotalMinutes:F0} min (limit {minutes.Value} min) — run force-stopped to release the concurrency group"));
            }
            catch { /* a throwing OnEvent subscriber must not stop reaping */ }
            try { run.Cancellation.Cancel(); } catch { /* best-effort */ }
            _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            reaped.Add(run);
        }
        return reaped;
    }
}
