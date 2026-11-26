using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace KittyClaw.Web.Services;

public sealed record AssociatedAccount(string Id, string Name, string? Email);
public sealed record AccountAssociationSnapshot(AssociatedAccount? Account, AssociatedAccount? ProposedAccount,
    string? AuthorizationUrl, long ExpiresAt, bool Revoked, bool MfaEnabled = false);
internal sealed record AccountInstallation(string InstanceId, string Credential, AssociatedAccount? Account = null,
    string? AuthorizationUrl = null, long ExpiresAt = 0, bool Revoked = false);
internal sealed record AssociationStart(string AuthorizationUrl, long ExpiresAt);
internal sealed record AssociationStatus(AssociatedAccount? Account, AssociatedAccount? ProposedAccount, bool Revoked, bool MfaEnabled = false);
public sealed record AccountAccessProof(string Proof, long ExpiresAt);
internal sealed record AccountAccessCheck(bool Valid, long ExpiresAt);
internal sealed record NetworkAccessStart(string AuthorizationUrl);

/// <summary>Account ownership is independent of tunnel allocation and network authentication.</summary>
public sealed class AccountAssociationService : BackgroundService
{
    public const string PortalOrigin = "https://my.kittyclaw.dev";
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly HttpClient _http;
    private readonly RemoteAccessStore? _remote;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccountInstallation? _installation;
    private AccountAssociationSnapshot _snapshot = new(null, null, null, 0, false);
    public AccountAssociationSnapshot Snapshot => _snapshot;
    public event Action? SecurityChanged;
    private long _securityVersion;
    internal long SecurityVersion => Interlocked.Read(ref _securityVersion);

    public AccountAssociationService(string dataDirectory, IDataProtectionProvider protection, HttpClient http, RemoteAccessStore? remote = null)
    {
        _path = Path.Combine(dataDirectory, "account-association.json");
        _protector = protection.CreateProtector("KittyClaw.AccountAssociation.v1");
        _http = http;
        _remote = remote;
        if (!File.Exists(_path)) return;
        _installation = JsonSerializer.Deserialize<AccountInstallation>(_protector.Unprotect(File.ReadAllText(_path)))
            ?? throw new InvalidDataException("Invalid account association state.");
        if (!Regex.IsMatch(_installation.InstanceId, "^[a-f0-9]{32}$")
            || !Regex.IsMatch(_installation.Credential, "^[a-f0-9]{64}$")
            || (_installation.AuthorizationUrl is not null && !ValidAuthorizationUrl(_installation.AuthorizationUrl)))
            throw new InvalidDataException("Invalid account association state.");
        Publish();
    }

    public async Task BeginAssociationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_installation is { Account: not null, Revoked: false }) throw new AccountAssociationException();
            if (_installation is null)
                Save(new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
            var result = await SendAsync<AssociationStart>("associate", new
            {
                instanceId = _installation!.InstanceId, name = Environment.MachineName,
                platform = Platform, version = Version
            }, cancellationToken);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!ValidAuthorizationUrl(result.AuthorizationUrl) || result.ExpiresAt <= timestamp || result.ExpiresAt > timestamp + 660)
                throw new AccountAssociationException();
            Save(_installation with { AuthorizationUrl = result.AuthorizationUrl, ExpiresAt = result.ExpiresAt });
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_installation is null) return;
            var status = await SendAsync<AssociationStatus>("status", new { instanceId = _installation.InstanceId }, cancellationToken);
            ValidateAccount(status.Account);
            ValidateAccount(status.ProposedAccount);
            Save(_installation with { Account = status.Account, Revoked = status.Revoked }, status.MfaEnabled);
            _snapshot = _snapshot with { ProposedAccount = status.ProposedAccount };
        }
        finally { _gate.Release(); }
    }

    public async Task ConfirmAsync(string accountId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_installation is null || _snapshot.ProposedAccount is not { } proposed || proposed.Id != accountId)
                throw new AccountAssociationException();
            await SendAsync<JsonElement>("confirm", new { instanceId = _installation.InstanceId, accountId }, cancellationToken);
            Save(_installation with { Account = proposed, AuthorizationUrl = null, ExpiresAt = 0, Revoked = false });
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_installation is null) return;
            await SendAsync<JsonElement>("disconnect", new { instanceId = _installation.InstanceId }, cancellationToken);
            Save(_installation with { Account = null, AuthorizationUrl = null, ExpiresAt = 0, Revoked = true });
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountAccessProof> RedeemAccessAsync(string code, string verifier, CancellationToken ct)
        => await RedeemAccessAsync(code, verifier, "tunnel", null, ct);
    internal async Task<string> BeginNetworkAccessAsync(string origin, string state, string challenge, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_installation?.Account is null || !_snapshot.MfaEnabled) throw new AccountAssociationException();
            var result = await SendAsync<NetworkAccessStart>("access/start", new { instanceId = _installation.InstanceId, kind = "network", origin, state, challenge }, ct);
            if (!Regex.IsMatch(result.AuthorizationUrl, $"^{Regex.Escape(PortalOrigin)}/instances/{_installation.InstanceId}/authorize\\?request=[a-f0-9]{{64}}$"))
                throw new AccountAssociationException();
            return result.AuthorizationUrl;
        }
        finally { _gate.Release(); }
    }
    internal async Task<AccountAccessProof> RedeemAccessAsync(string code, string verifier, string kind, string? origin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_installation?.Account is null || !_snapshot.MfaEnabled) throw new AccountAssociationException();
            var proof = await SendAsync<AccountAccessProof>("access/redeem", new { instanceId = _installation.InstanceId, code, verifier, kind, origin }, ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!Regex.IsMatch(proof.Proof, "^[a-f0-9]{64}$") || proof.ExpiresAt <= now || proof.ExpiresAt > now + 43260)
                throw new AccountAssociationException();
            return proof;
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> CheckAccessAsync(string proof, CancellationToken ct)
        => await CheckAccessAsync(proof, "tunnel", null, ct);
    internal async Task<bool> CheckAccessAsync(string proof, string kind, string? origin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_installation?.Account is null || !_snapshot.MfaEnabled) return false;
            var check = await SendAsync<AccountAccessCheck>("access/check", new { instanceId = _installation.InstanceId, proof, kind, origin }, ct);
            return check.Valid && check.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        finally { _gate.Release(); }
    }
    public async Task RevokeAccessAsync(string proof, CancellationToken ct)
        => await RevokeAccessAsync(proof, "tunnel", null, ct);
    internal async Task RevokeAccessAsync(string proof, string kind, string? origin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_installation?.Account is not null)
                await SendAsync<JsonElement>("access/revoke", new { instanceId = _installation.InstanceId, proof, kind, origin }, ct);
        }
        finally { _gate.Release(); }
    }
    internal string? InstallationId => _installation?.InstanceId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Snapshot.Account is not null)
                {
                    await RefreshAsync(stoppingToken);
                    await _gate.WaitAsync(stoppingToken);
                    try
                    {
                        if (_installation is { Account: not null, Revoked: false })
                        {
                            var remote = _remote?.Snapshot;
                            await SendAsync<JsonElement>("heartbeat", new { instanceId = _installation.InstanceId, platform = Platform, version = Version,
                                remoteUrl = remote?.Url, remoteEnabled = remote is { Enabled: true, Connected: true } }, stoppingToken);
                        }
                    }
                    finally { _gate.Release(); }
                }
            }
            catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException)
            {
                // Retry transient failures without logging account or installation credentials.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<T> SendAsync<T>(string action, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PortalOrigin}/v1/installations/{action}");
        request.Headers.Authorization = new("Bearer", _installation!.Credential);
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AccountAssociationException();
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new AccountAssociationException();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        { throw new AccountAssociationException(); }
    }

    private void Save(AccountInstallation installation, bool? mfaEnabled = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(installation)));
        File.Move(temporary, _path, true);
        _installation = installation;
        Publish(mfaEnabled);
    }

    private void Publish(bool? mfaEnabled = null)
    {
        var pending = _installation!.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var previous = _snapshot;
        _snapshot = new(_installation.Account, null, pending ? _installation.AuthorizationUrl : null, _installation.ExpiresAt, _installation.Revoked,
            _installation.Account is not null && (mfaEnabled ?? (previous.Account?.Id == _installation.Account.Id && previous.MfaEnabled)));
        if (previous.Account?.Id != _snapshot.Account?.Id || previous.MfaEnabled != _snapshot.MfaEnabled)
        {
            Interlocked.Increment(ref _securityVersion);
            SecurityChanged?.Invoke();
        }
    }

    private static bool ValidAuthorizationUrl(string value) => Regex.IsMatch(value,
        "^https://my\\.kittyclaw\\.dev/associate\\?request=[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static void ValidateAccount(AssociatedAccount? account)
    {
        if (account is not null && (!Guid.TryParse(account.Id, out _) || string.IsNullOrWhiteSpace(account.Name)
            || account.Name.Length > 200 || account.Email?.Length > 320)) throw new AccountAssociationException();
    }
    private static string Platform => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
    private static string Version => typeof(AccountAssociationService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}

public sealed class AccountAssociationException : Exception
{
    public AccountAssociationException() : base("Account association could not be completed.") { }
}
