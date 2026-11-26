using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace KittyClaw.Web.Services;

internal sealed record PortalChallenge(string State, string Verifier, string InstanceId, string AccountId, long ExpiresAt);
internal sealed record PortalSession(string Proof, string InstanceId, string AccountId, long ExpiresAt);
internal sealed class AccountTunnelAuthenticated;

/// <summary>Host-only browser sessions backed by a live, centrally verified account session.</summary>
public sealed class AccountPortalAccessService(AccountAssociationService accounts, RemoteAccessStore remote, IDataProtectionProvider protection)
{
    private readonly IDataProtector _challenge = protection.CreateProtector("KittyClaw.PortalChallenge.v1");
    private readonly IDataProtector _session = protection.CreateProtector("KittyClaw.PortalSession.v1");
    private readonly SemaphoreSlim _checks = new(1, 1);
    private readonly ConcurrentDictionary<string, (bool Valid, DateTimeOffset Until, long Version)> _cache = new();
    internal bool Available => accounts.Snapshot is { Account: not null, MfaEnabled: true } && remote.Snapshot.Enabled;
    internal event Action SecurityChanged { add => accounts.SecurityChanged += value; remove => accounts.SecurityChanged -= value; }
    internal async Task RefreshAsync(CancellationToken ct) => await accounts.RefreshAsync(ct);

    internal (string Cookie, string Url) Begin(DateTimeOffset now)
    {
        if (!Available || accounts.InstallationId is not { } id || accounts.Snapshot.Account is not { } account)
            throw new AccountAssociationException();
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var value = new PortalChallenge(state, verifier, id, account.Id, now.AddMinutes(5).ToUnixTimeSeconds());
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (_challenge.Protect(JsonSerializer.Serialize(value)),
            $"{AccountAssociationService.PortalOrigin}/instances/{id}/authorize?state={state}&challenge={challenge}");
    }
    internal async Task<string> CompleteAsync(string? cookie, string state, string code, DateTimeOffset now, CancellationToken ct)
    {
        var challenge = Read<PortalChallenge>(_challenge, cookie);
        if (!Available || challenge is null || challenge.ExpiresAt <= now.ToUnixTimeSeconds()
            || challenge.InstanceId != accounts.InstallationId || challenge.AccountId != accounts.Snapshot.Account?.Id
            || !Regex.IsMatch(code, "^[a-f0-9]{64}$") || !Regex.IsMatch(state, "^[a-f0-9]{64}$")
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(challenge.State)))
            throw new AccountAssociationException();
        var proof = await accounts.RedeemAccessAsync(code, challenge.Verifier, ct);
        return _session.Protect(JsonSerializer.Serialize(new PortalSession(proof.Proof, challenge.InstanceId, challenge.AccountId, proof.ExpiresAt)));
    }
    internal PortalSession? ReadSession(string? cookie, DateTimeOffset now)
    {
        if (!Available) return null;
        var session = Read<PortalSession>(_session, cookie);
        return session is not null && session.InstanceId == accounts.InstallationId && session.AccountId == accounts.Snapshot.Account?.Id
            && session.ExpiresAt > now.ToUnixTimeSeconds() && session.ExpiresAt <= now.AddHours(12).AddMinutes(1).ToUnixTimeSeconds()
            && Regex.IsMatch(session.Proof, "^[a-f0-9]{64}$") ? session : null;
    }
    internal async Task<bool> ValidAsync(string? cookie, CancellationToken ct)
    {
        var session = ReadSession(cookie, DateTimeOffset.UtcNow);
        if (session is null) return false;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(session.Proof)));
        await _checks.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var version = accounts.SecurityVersion;
            if (_cache.TryGetValue(key, out var cached) && cached.Until > now && cached.Version == version) return cached.Valid;
            var valid = false;
            try { valid = await accounts.CheckAccessAsync(session.Proof, ct); }
            catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException) { }
            if (_cache.Count > 512)
                foreach (var item in _cache.Where(item => item.Value.Until <= now)) _cache.TryRemove(item.Key, out _);
            valid = valid && Available && version == accounts.SecurityVersion;
            _cache[key] = (valid, now.AddSeconds(15), version);
            return valid;
        }
        finally { _checks.Release(); }
    }
    internal async Task RevokeAsync(string? cookie, CancellationToken ct)
    {
        var session = ReadSession(cookie, DateTimeOffset.UtcNow);
        if (session is null) return;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(session.Proof)));
        _cache[key] = (false, DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt), accounts.SecurityVersion);
        try { await accounts.RevokeAccessAsync(session.Proof, ct); }
        catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException) { }
    }
    private static T? Read<T>(IDataProtector protector, string? cookie)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > 4096) return default;
        try { return JsonSerializer.Deserialize<T>(protector.Unprotect(cookie)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return default; }
    }
}
