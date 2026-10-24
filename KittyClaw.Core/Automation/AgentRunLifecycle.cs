using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Automation;

/// <summary>Bounded cancellation and cleanup shared by every agent launch path.</summary>
public sealed class AgentRunLifecycle(AgentRunRegistry runs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            runs.PurgeOld(TimeSpan.FromHours(24));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var active = runs.AllActive().ToArray();
        foreach (var run in active) run.Cancellation.Cancel();
        await base.StopAsync(cancellationToken);
        if (runs.IsUpdating)
            await runs.WaitForExecutionsAsync(cancellationToken);
        else
            await WaitForRunsAsync(active, cancellationToken);
    }

    public static Task CancelAndWaitForRunsAsync(IEnumerable<AgentRun> runs, CancellationToken ct)
    {
        var snapshot = runs.ToArray();
        foreach (var run in snapshot) run.Cancellation.Cancel();
        return WaitForRunsAsync(snapshot, ct);
    }

    public static async Task WaitForRunsAsync(IEnumerable<AgentRun> runs, CancellationToken ct)
    {
        var snapshot = runs.ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (snapshot.Any(run => run.Status == AgentRunStatus.Running) && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);
        var remaining = snapshot.Where(run => run.Status == AgentRunStatus.Running).Select(run => run.RunId).ToArray();
        if (remaining.Length > 0)
            throw new TimeoutException($"Run cleanup did not finish within 10 seconds: {string.Join(", ", remaining)}");
    }
}
