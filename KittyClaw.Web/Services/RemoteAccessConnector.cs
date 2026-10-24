using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

public sealed record RemoteConnectorStatus(string State, string? Error = null);

public sealed class RemoteAccessConnector(RemoteAccessStore store, RemoteAccessBroker broker,
    ICloudflaredRuntime runtime, RemoteTunnelOptions origin, NetworkAccessService access,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _connection;
    private RemoteConnectorStatus _status = new("Disabled");
    public RemoteConnectorStatus Status => Volatile.Read(ref _status);
    public bool NeedsRestart => store.Snapshot.Registered && (!origin.Enabled || origin.Hostname != new Uri(store.Snapshot.Url!).Host);
    private void SetStatus(string state, string? error = null) => Volatile.Write(ref _status, new(state, error));
    private void Wake() { if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { } }

    public async Task ActivateAsync(CancellationToken ct, string? subdomain = null)
    {
        await _commands.WaitAsync(ct);
        try
        {
            if (!access.Enabled) throw new RemoteAccessException("RemoteAuthenticationRequired");
            var identity = store.Prepare(broker.Origin, origin.Port, subdomain);
            if (!identity.Registered)
            {
                try { await broker.RegisterAsync(identity, ct); }
                catch (RemoteAccessException error) when (error.Code is "RemoteNameUnavailable" or "RemoteNameInvalid")
                { store.ReleaseNameForRetry(); throw; }
            }
            store.Enable();
            SetStatus(NeedsRestart ? "RestartRequired" : "Connecting");
            Wake();
        }
        finally { _commands.Release(); }
    }
    public async Task PauseAsync(CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            store.Pause(); // The HTTP gate closes before stopping the process.
            lock (_gate) _connection?.Cancel();
            access.RevokeSessions();
            SetStatus("Disabled");
            Wake();
        }
        finally { _commands.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not provision or start a connector before the web listener is ready.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var applicationStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var stopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        await started.Task;
        void Revoke() { lock (_gate) _connection?.Cancel(); Wake(); }
        access.SessionsRevoked += Revoke;
        var attempts = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var snapshot = store.Snapshot;
                if (!snapshot.Enabled || !access.Enabled || NeedsRestart)
                {
                    SetStatus(!snapshot.Enabled ? "Disabled" : !access.Enabled ? "AuthenticationRequired" : "RestartRequired");
                    await _wake.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_gate)
                {
                    _connection = connection;
                    if (!store.Snapshot.Enabled || !access.Enabled) connection.Cancel();
                }
                try
                {
                    SetStatus("Connecting");
                    var token = await broker.ConnectAsync(store.Identity!, connection.Token);
                    SetStatus("Installing");
                    await runtime.RunAsync(token, ready =>
                    {
                        if (!connection.IsCancellationRequested)
                        {
                            SetStatus(ready ? "Online" : "Connecting");
                            if (ready) attempts = 0;
                        }
                    }, connection.Token);
                    if (!connection.IsCancellationRequested) SetStatus("Retrying", "RemoteConnectorUnavailable");
                }
                catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
                catch (RemoteAccessException error)
                {
                    if (error.Code is "RemoteRetired" or "RemoteOwnershipInvalid" or "RemoteIdentityMismatch"
                        or "RemotePlatformUnsupported" or "RemoteBrokerInvalid")
                    {
                        SetStatus("Error", error.Code);
                        await _wake.WaitAsync(stoppingToken);
                    }
                    else SetStatus("Retrying", error.Code);
                }
                catch (Exception) { SetStatus("Retrying", "RemoteConnectorUnavailable"); }
                finally { lock (_gate) _connection = null; }
                if (stoppingToken.IsCancellationRequested) break;
                if (!store.Snapshot.Enabled || !access.Enabled) continue;
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(++attempts, 6))) + Random.Shared.NextDouble());
                await _wake.WaitAsync(delay, stoppingToken);
            }
        }
        finally { access.SessionsRevoked -= Revoke; }
    }
}
