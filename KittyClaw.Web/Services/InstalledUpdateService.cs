using KittyClaw.Core.Automation;
using Velopack;
using Velopack.Sources;

namespace KittyClaw.Web.Services;

public interface IInstalledUpdateClient
{
    bool IsSupported { get; }
    string? PendingVersion { get; }
    Task<string?> CheckAsync();
    Task DownloadAsync(Action<int> progress, CancellationToken ct);
    void PrepareRestart();
}

public sealed class VelopackUpdateClient : IInstalledUpdateClient
{
    private readonly UpdateManager? _manager = OperatingSystem.IsWindows()
        ? new UpdateManager(new GithubSource("https://github.com/Ekioo/KittyClaw", null, false))
        : null;
    private UpdateInfo? _update;

    public bool IsSupported => _manager?.IsInstalled == true;
    public string? PendingVersion => IsSupported ? _manager!.UpdatePendingRestart?.Version.ToString() : null;

    public async Task<string?> CheckAsync()
    {
        if (!IsSupported) return null;
        _update = await _manager!.CheckForUpdatesAsync();
        return _update?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken ct) =>
        _manager!.DownloadUpdatesAsync(_update ?? throw new InvalidOperationException("Check for updates first."), progress, ct);

    public void PrepareRestart() =>
        _manager!.WaitExitThenApplyUpdates(_manager.UpdatePendingRestart
            ?? throw new InvalidOperationException("No downloaded update is available."), silent: false, restart: true);
}

/// <summary>Owns the download/restart workflow; source installations keep the release-link banner.</summary>
public sealed class InstalledUpdateService(
    IInstalledUpdateClient client,
    AgentRunRegistry runs,
    IHostApplicationLifetime lifetime,
    ILogger<InstalledUpdateService> logger)
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    public bool IsSupported => client.IsSupported;
    public string? AvailableVersion { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsReady => client.PendingVersion is not null;
    public bool IsRestarting { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public event Action? OnChange;

    public async Task<bool> CheckAsync()
    {
        if (!IsSupported || IsRestarting || !await _operation.WaitAsync(0)) return false;
        try
        {
            Error = null;
            // A prepared package remains actionable if GitHub is temporarily unavailable.
            AvailableVersion = client.PendingVersion ?? await client.CheckAsync();
            if (AvailableVersion is null) return true;
            if (!IsReady)
            {
                IsBusy = true;
                Progress = 0;
                OnChange?.Invoke();
                await client.DownloadAsync(p => { Progress = p; OnChange?.Invoke(); }, lifetime.ApplicationStopping);
            }
            return true;
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update download failed");
            Error = AvailableVersion is null ? null
                : "The update could not be downloaded. KittyClaw will retry automatically at the next update check.";
            return false;
        }
        finally
        {
            IsBusy = false;
            _operation.Release();
            OnChange?.Invoke();
        }
    }

    public void InstallAndRestart()
    {
        if (!IsSupported || IsRestarting || !_operation.Wait(0)) return;
        var gated = false;
        try
        {
            Error = null;
            if (!IsReady) return;
            if (!(gated = runs.TryBeginUpdate()))
            {
                Error = "Agents are still running. Wait for them to finish, then try again.";
                return;
            }
            // The updater waits for process exit; stop the host gracefully to flush SQLite
            // and hosted services rather than using ApplyUpdatesAndRestart's immediate exit.
            client.PrepareRestart();
            IsRestarting = true;
            lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not start the updater");
            Error = "The update could not be installed. Please try again.";
        }
        finally
        {
            if (gated && !IsRestarting) runs.CancelUpdate();
            _operation.Release();
            OnChange?.Invoke();
        }
    }
}
