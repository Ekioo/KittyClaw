namespace KittyClaw.Core.Tests.Automation;

public sealed class AgentRunLifecycleTests
{
    [Fact]
    public async Task Shutdown_CancelsEveryActiveRun_AndWaitsForCleanup()
    {
        var registry = new AgentRunRegistry();
        var first = NewRun("first");
        var second = NewRun("second");
        registry.Register(first);
        registry.Register(second);
        using var cleanupStarted = new SemaphoreSlim(0);
        using var finishCleanup = new SemaphoreSlim(0);
        async Task CleanupAsync(AgentRun run)
        {
            try { await Task.Delay(Timeout.Infinite, run.Cancellation.Token); }
            catch (OperationCanceledException) { }
            cleanupStarted.Release();
            await finishCleanup.WaitAsync();
            registry.Complete(run.RunId, AgentRunStatus.Stopped, -1);
        }
        var workers = new[] { CleanupAsync(first), CleanupAsync(second) };
        using var service = new AgentRunLifecycle(registry);
        await service.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync(timeout.Token);
        await cleanupStarted.WaitAsync(timeout.Token);
        await cleanupStarted.WaitAsync(timeout.Token);
        Assert.False(stopping.IsCompleted);
        finishCleanup.Release(2);
        await stopping;
        await Task.WhenAll(workers);
        Assert.Empty(registry.AllActive());
        await AgentRunLifecycle.CancelAndWaitForRunsAsync([first, second], timeout.Token);
    }

    private static AgentRun NewRun(string id) => new()
    {
        RunId = id,
        ProjectSlug = id,
        AgentName = "processor",
        TicketId = null,
        SkillFile = "processor/SKILL.md",
        ConcurrencyGroup = id,
        StartedAt = DateTime.UtcNow,
    };
}
