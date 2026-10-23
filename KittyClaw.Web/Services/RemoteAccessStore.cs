using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace KittyClaw.Web.Services;

public sealed record RemoteAccessSnapshot(bool Registered, bool Enabled, string? Url, int Port);
internal sealed record RemoteAccessIdentity(string InstanceId, string Credential, string BrokerOrigin,
    int Port, bool Registered = false, bool Enabled = false);

/// <summary>Installation ownership is persisted before any remote allocation.</summary>
public sealed class RemoteAccessStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IDataProtector _protector;
    private RemoteAccessIdentity? _identity;
    public string DataDirectory { get; }

    public RemoteAccessStore(string dataDirectory, IDataProtectionProvider protection)
    {
        DataDirectory = dataDirectory;
        _path = Path.Combine(dataDirectory, "remote-access.json");
        _protector = protection.CreateProtector("KittyClaw.RemoteAccess.v1");
        if (!File.Exists(_path)) return;
        _identity = JsonSerializer.Deserialize<RemoteAccessIdentity>(_protector.Unprotect(File.ReadAllText(_path)))
            ?? throw new InvalidDataException("Invalid remote access state.");
        if (!Regex.IsMatch(_identity.InstanceId, "^[a-f0-9]{32}$")
            || !Regex.IsMatch(_identity.Credential, "^[a-f0-9]{64}$")
            || _identity.Port is < 1024 or > 65535
            || !Uri.TryCreate(_identity.BrokerOrigin, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("Invalid remote access state.");
    }

    public RemoteAccessSnapshot Snapshot
    {
        get { lock (_gate) return new(_identity?.Registered == true, _identity?.Enabled == true,
            _identity?.Registered == true ? $"https://kc-{_identity.InstanceId}.kittyclaw.dev" : null, _identity?.Port ?? 5234); }
    }
    internal RemoteAccessIdentity? Identity { get { lock (_gate) return _identity; } }
    internal RemoteAccessIdentity Prepare(string brokerOrigin, int port)
    {
        lock (_gate)
        {
            if (port is < 1024 or > 65535) throw new RemoteAccessException("RemotePortInvalid");
            if (_identity is not null)
            {
                if (_identity.BrokerOrigin != brokerOrigin || _identity.Port != port)
                    throw new RemoteAccessException("RemoteIdentityMismatch");
                return _identity;
            }
            return Save(new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), brokerOrigin, port));
        }
    }
    internal void Enable()
    {
        lock (_gate) Save((_identity ?? throw new InvalidOperationException()) with { Registered = true, Enabled = true });
    }
    internal void Pause()
    {
        lock (_gate) { if (_identity is not null) Save(_identity with { Enabled = false }); }
    }
    private RemoteAccessIdentity Save(RemoteAccessIdentity identity)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(identity)));
        File.Move(temporary, _path, true);
        return _identity = identity;
    }
    internal RemoteTunnelOptions OriginOptions(RemoteTunnelOptions configured)
    {
        lock (_gate) return _identity?.Registered == true
            ? new(true, _identity.Port, $"kc-{_identity.InstanceId}.kittyclaw.dev") : configured;
    }
}

public sealed class RemoteAccessException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
