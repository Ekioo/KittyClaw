using KittyClaw.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public class InstalledUpdateServiceTests
{
    [Fact]
    public async Task Download_DoesNotStopHost_AndRestartWaitsForAgentCompletion()
    {
        var client = new FakeClient();
        var runs = new AgentRunRegistry();
        var lifetime = new FakeLifetime();
        var service = Create(client, runs, lifetime);
        await service.DownloadAsync();
        Assert.True(service.IsReady);
        Assert.Equal(100, service.Progress);
        Assert.False(lifetime.Stopped);

        var run = NewRun();
        runs.Register(run);
        service.InstallAndRestart();
        Assert.Contains("Agents are still running", service.Error);
        Assert.False(client.Prepared);
        Assert.False(lifetime.Stopped);

        runs.Complete(run.RunId, AgentRunStatus.Completed, 0);
        service.InstallAndRestart();
        Assert.True(client.Prepared);
        Assert.True(service.IsRestarting);
        Assert.True(lifetime.Stopped);
        Assert.Throws<InvalidOperationException>(() => runs.Register(NewRun()));
    }

    [Fact]
    public void Restart_AlsoWaitsForPostRunFinalization()
    {
        var runs = new AgentRunRegistry();
        var service = Create(new FakeClient { PendingVersion = "1.1.0" }, runs, new FakeLifetime());
        var execution = runs.TrackExecution();
        service.InstallAndRestart();
        Assert.False(service.IsRestarting);
        execution.Dispose();
        service.InstallAndRestart();
        Assert.True(service.IsRestarting);
    }

    [Fact]
    public void FailedUpdater_ReleasesDispatchGate_AndCanBeRetried()
    {
        var client = new FakeClient { PendingVersion = "1.1.0", FailPrepare = true };
        var runs = new AgentRunRegistry();
        var lifetime = new FakeLifetime();
        var service = Create(client, runs, lifetime);
        service.InstallAndRestart();
        Assert.NotNull(service.Error);
        Assert.False(lifetime.Stopped);
        var run = runs.Register(NewRun());
        runs.Complete(run.RunId, AgentRunStatus.Completed, 0);
        client.FailPrepare = false;
        service.InstallAndRestart();
        Assert.True(lifetime.Stopped);
    }

    [Fact]
    public async Task DownloadFailure_CanBeRetried_WithoutRestarting()
    {
        var client = new FakeClient { FailDownload = true };
        var lifetime = new FakeLifetime();
        var service = Create(client, new AgentRunRegistry(), lifetime);
        await service.DownloadAsync();
        Assert.False(service.IsBusy);
        Assert.False(service.IsReady);
        Assert.NotNull(service.Error);
        Assert.False(lifetime.Stopped);
        client.FailDownload = false;
        await service.DownloadAsync();
        Assert.True(service.IsReady);
        Assert.Null(service.Error);
    }

    [Fact]
    public async Task PendingPackage_RemainsAvailableOfflineAfterRestart()
    {
        var client = new FakeClient { PendingVersion = "1.1.0", FailCheck = true };
        var service = Create(client, new AgentRunRegistry(), new FakeLifetime());
        await service.CheckAsync();
        Assert.Equal("1.1.0", service.AvailableVersion);
        Assert.True(service.IsReady);
        service.InstallAndRestart();
        Assert.True(service.IsRestarting);
    }

    [Fact]
    public async Task SourceInstallation_CannotDownloadOrRestart()
    {
        var client = new FakeClient { IsSupported = false, PendingVersion = "1.1.0" };
        var lifetime = new FakeLifetime();
        var service = Create(client, new AgentRunRegistry(), lifetime);
        await service.CheckAsync();
        await service.DownloadAsync();
        service.InstallAndRestart();
        Assert.Equal(0, client.CheckCount);
        Assert.False(client.Prepared);
        Assert.False(lifetime.Stopped);
    }

    [Fact]
    public async Task ConcurrentDownloadClicks_OnlyStartOneDownload()
    {
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient { DownloadBarrier = releaseDownload.Task };
        var service = Create(client, new AgentRunRegistry(), new FakeLifetime());
        var first = service.DownloadAsync();
        await service.DownloadAsync();
        Assert.Equal(1, client.DownloadCount);
        releaseDownload.SetResult();
        await first;
        Assert.True(service.IsReady);
    }

    [Fact]
    public void DispatchAndUpdate_CannotBothAcquireAnIdleRegistry()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var runs = new AgentRunRegistry();
            bool updating = false, dispatched = false;
            Parallel.Invoke(
                () => updating = runs.TryBeginUpdate(),
                () =>
                {
                    try { runs.Register(NewRun()); dispatched = true; }
                    catch (InvalidOperationException) { }
                });
            Assert.NotEqual(updating, dispatched);
        }
    }

    private static InstalledUpdateService Create(FakeClient client, AgentRunRegistry runs, FakeLifetime lifetime) =>
        new(client, runs, lifetime, NullLogger<InstalledUpdateService>.Instance);

    private static AgentRun NewRun() => new()
    {
        RunId = Guid.NewGuid().ToString("N"), ProjectSlug = "test", TicketId = null,
        AgentName = "test", SkillFile = "test/SKILL.md", ConcurrencyGroup = "test", StartedAt = DateTime.UtcNow,
    };

    private sealed class FakeClient : IInstalledUpdateClient
    {
        public bool IsSupported { get; set; } = true;
        public string? PendingVersion { get; set; }
        public bool FailPrepare { get; set; }
        public bool FailDownload { get; set; }
        public bool FailCheck { get; set; }
        public bool Prepared { get; private set; }
        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }
        public Task? DownloadBarrier { get; init; }
        public Task<string?> CheckAsync()
        {
            CheckCount++;
            if (FailCheck) throw new IOException("Offline");
            return Task.FromResult<string?>("1.1.0");
        }
        public async Task DownloadAsync(Action<int> progress, CancellationToken ct)
        {
            DownloadCount++;
            if (FailDownload) throw new IOException("Offline");
            if (DownloadBarrier is not null) await DownloadBarrier.WaitAsync(ct);
            progress(100);
            PendingVersion = "1.1.0";
        }
        public void PrepareRestart()
        {
            if (FailPrepare) throw new IOException("Updater unavailable");
            Prepared = true;
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }
}
