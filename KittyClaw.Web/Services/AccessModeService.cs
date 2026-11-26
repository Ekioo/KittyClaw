namespace KittyClaw.Web.Services;

public enum InstanceAccessMode { Local, Network, Internet }

/// <summary>One owner-selected access surface, using existing protected opt-in state.</summary>
public sealed class AccessModeService
{
    private readonly NetworkAccessService network;
    private readonly RemoteAccessStore remote;
    private readonly RemoteAccessConnector connector;
    private readonly AccountAssociationService accounts;
    private readonly SemaphoreSlim _commands = new(1, 1);
    public AccessModeService(NetworkAccessService network, RemoteAccessStore remote,
        RemoteAccessConnector connector, AccountAssociationService accounts)
    {
        this.network = network; this.remote = remote; this.connector = connector; this.accounts = accounts;
        // Older versions allowed both independent toggles. Keep their Internet
        // opt-in and narrow the redundant LAN surface when migrating to one mode.
        if (remote.Snapshot.Enabled && network.Enabled) network.Disable();
    }
    public InstanceAccessMode Mode => remote.Snapshot.Enabled ? InstanceAccessMode.Internet
        : network.AccountAccessEnabled ? InstanceAccessMode.Network : InstanceAccessMode.Local;
    public bool AccountReady => accounts.Snapshot is { Account: not null, MfaEnabled: true };
    public string StatusKey => Mode switch
    {
        InstanceAccessMode.Network => network.Listening ? "NetworkEnabled" : "NetworkRestart",
        InstanceAccessMode.Internet => "RemoteStatus" + (!AccountReady ? "AuthenticationRequired"
            : connector.NeedsRestart ? "RestartRequired" : connector.Status.State),
        _ => "AccessHintLocal"
    };
    public async Task ApplyAsync(InstanceAccessMode mode, bool consent, string? subdomain, CancellationToken ct)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Invalid access mode.", nameof(mode));
        await _commands.WaitAsync(ct);
        try
        {
            // Recovery to local-only is always possible, even during a portal outage.
            if (mode != InstanceAccessMode.Local)
            {
                try { await accounts.RefreshAsync(ct); }
                catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException)
                { throw new RemoteAccessException("AccountError"); }
                if (!AccountReady) throw new RemoteAccessException("AccountNetworkRequired");
            }
            if (mode == InstanceAccessMode.Internet)
            {
                if (!consent && !remote.Snapshot.Enabled) throw new RemoteAccessException("AccessConsentRequired");
                // Keep the previous LAN mode if provisioning fails. The connector
                // does not enable access until it has validated the broker response.
                if (!remote.Snapshot.Enabled) await connector.ActivateAsync(ct, subdomain);
                if (network.Enabled) network.Disable();
            }
            else
            {
                if (remote.Snapshot.Enabled) await connector.PauseAsync(ct);
                if (mode == InstanceAccessMode.Network)
                {
                    if (!network.AccountAccessEnabled) network.EnableAccountAccess();
                }
                else if (network.Enabled) network.Disable();
            }
        }
        finally { _commands.Release(); }
    }
}
