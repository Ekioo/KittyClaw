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
    string? AuthorizationUrl, long ExpiresAt, bool Revoked);
internal sealed record AccountInstallation(string InstanceId, string Credential, AssociatedAccount? Account = null,
    string? AuthorizationUrl = null, long ExpiresAt = 0, bool Revoked = false);
internal sealed record AssociationStart(string AuthorizationUrl, long ExpiresAt);
internal sealed record AssociationStatus(AssociatedAccount? Account, AssociatedAccount? ProposedAccount, bool Revoked);

/// <summary>Account ownership is independent of tunnel allocation and network authentication.</summary>
public sealed class AccountAssociationService : BackgroundService
{
    public const string PortalOrigin = "https://my.kittyclaw.dev";
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccountInstallation? _installation;
    private AccountAssociationSnapshot _snapshot = new(null, null, null, 0, false);
    public AccountAssociationSnapshot Snapshot => _snapshot;

    public AccountAssociationService(string dataDirectory, IDataProtectionProvider protection, HttpClient http)
    {
        _path = Path.Combine(dataDirectory, "account-association.json");
        _protector = protection.CreateProtector("KittyClaw.AccountAssociation.v1");
        _http = http;
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
            Save(_installation with { Account = status.Account, Revoked = status.Revoked });
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
                            await SendAsync<JsonElement>("heartbeat", new { instanceId = _installation.InstanceId, platform = Platform, version = Version }, stoppingToken);
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

    private void Save(AccountInstallation installation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(installation)));
        File.Move(temporary, _path, true);
        _installation = installation;
        Publish();
    }

    private void Publish()
    {
        var pending = _installation!.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _snapshot = new(_installation.Account, null, pending ? _installation.AuthorizationUrl : null, _installation.ExpiresAt, _installation.Revoked);
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
