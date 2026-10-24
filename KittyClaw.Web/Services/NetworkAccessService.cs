using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace KittyClaw.Web.Services;

/// <summary>Opt-in authenticator-only network access. Secrets and sessions are protected at rest.</summary>
public sealed class NetworkAccessService
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IDataProtector _protector;
    private State _state = new();
    private const int MaximumLoginSources = 1024;
    private readonly Dictionary<string, LoginAttempt> _loginAttempts = new(StringComparer.Ordinal);
    public event Action? SessionsRevoked;
    public event Action<string>? SessionRevoked;
    public bool Enabled { get { lock (_gate) return _state.Secret is not null; } }
    public bool Listening { get; set; }
    public int Port { get; }
    public IReadOnlyList<IPAddress> Addresses { get; } = GetAddresses();

    public NetworkAccessService(string dataDir, IDataProtectionProvider protection, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Port = port;
        _path = Path.Combine(dataDir, "network-access.json");
        _protector = protection.CreateProtector("KittyClaw.NetworkAccess.v1");
        if (File.Exists(_path))
        {
            // Fail closed on unreadable state: never silently discard an enrolled authenticator.
            _state = JsonSerializer.Deserialize<State>(_protector.Unprotect(File.ReadAllText(_path)))
                ?? throw new InvalidDataException("Invalid network access settings.");
        }
    }

    public string CreateEnrollment() => _protector.Protect(JsonSerializer.Serialize(
        new Enrollment(Convert.ToHexString(RandomNumberGenerator.GetBytes(20)), DateTimeOffset.UtcNow.AddMinutes(10))));

    public string EnrollmentSecret(string token) => Base32(Convert.FromHexString(ReadEnrollment(token).Secret));

    private Enrollment ReadEnrollment(string token)
    {
        var enrollment = JsonSerializer.Deserialize<Enrollment>(_protector.Unprotect(token))!;
        if (enrollment.Expires < DateTimeOffset.UtcNow) throw new InvalidOperationException("Enrollment expired.");
        return enrollment;
    }

    public bool Enable(string token, string code)
    {
        lock (_gate)
        {
            if (Enabled) return false;
            var enrollment = ReadEnrollment(token);
            var step = MatchStep(enrollment.Secret, code, DateTimeOffset.UtcNow);
            if (step is null) return false;
            var next = new State { Secret = enrollment.Secret, LastStep = step.Value };
            Save(next);
            _state = next;
            return true;
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            var next = new State();
            Save(next);
            _state = next;
        }
        SessionsRevoked?.Invoke();
    }

    public void RevokeSessions()
    {
        lock (_gate)
        {
            var next = _state with { Generation = Guid.NewGuid().ToString("N"), RevokedSessions = [] };
            Save(next);
            _state = next;
        }
        SessionsRevoked?.Invoke();
    }

    public string? Authenticate(string code, DateTimeOffset now, string clientKey = "unspecified")
    {
        var source = SessionKey(clientKey);
        lock (_gate)
        {
            if (_state.Secret is null) return null;
            foreach (var key in _loginAttempts.Where(p => p.Value.LastAttempt < now.AddMinutes(-10)).Select(p => p.Key).ToArray())
                _loginAttempts.Remove(key);
            _loginAttempts.TryGetValue(source, out var attempt);
            if (attempt is not null && now < attempt.BlockedUntil) return null;
            var step = MatchStep(_state.Secret, code, now);
            if (step is null || step <= _state.LastStep)
            {
                if (attempt is null && _loginAttempts.Count >= MaximumLoginSources)
                    _loginAttempts.Remove(_loginAttempts.MinBy(p => p.Value.LastAttempt).Key);
                var failures = (attempt?.Failures ?? 0) + 1;
                _loginAttempts[source] = failures >= 5
                    ? new(0, now.AddMinutes(1), now) : new(failures, DateTimeOffset.MinValue, now);
                return null;
            }
            var next = _state with { LastStep = step.Value, RevokedSessions = ActiveRevocations(now) };
            Save(next);
            _state = next;
            _loginAttempts.Remove(source);
            return _protector.Protect(JsonSerializer.Serialize(new Session(_state.Generation, now.AddHours(12))));
        }
    }

    public bool ValidSession(string? token, DateTimeOffset now) => SessionExpiry(token, now) is not null;

    public DateTimeOffset? SessionExpiry(string? token, DateTimeOffset now)
    {
        var session = ReadSession(token);
        lock (_gate) return session is not null && Enabled && session.Generation == _state.Generation
            && session.Expires > now && !_state.RevokedSessions.ContainsKey(SessionKey(token!)) ? session.Expires : null;
    }

    public void RevokeSession(string? token, DateTimeOffset now)
    {
        string key;
        lock (_gate)
        {
            if (SessionExpiry(token, now) is null) return;
            var session = ReadSession(token)!;
            key = SessionKey(token!);
            var revoked = ActiveRevocations(now);
            revoked[key] = session.Expires;
            var next = _state with { RevokedSessions = revoked };
            Save(next);
            _state = next;
        }
        SessionRevoked?.Invoke(key);
    }

    // This identifier is only a revocation lookup key and never authenticates a request.
    internal static string SessionKey(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private Dictionary<string, DateTimeOffset> ActiveRevocations(DateTimeOffset now) =>
        _state.RevokedSessions.Where(p => p.Value > now).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private Session? ReadSession(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 8192) return null;
        try { return JsonSerializer.Deserialize<Session>(_protector.Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }

    private void Save(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(state)));
        File.Move(temporary, _path, overwrite: true);
    }

    internal static long? MatchStep(string secret, string code, DateTimeOffset now)
    {
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) return null;
        var step = now.ToUnixTimeSeconds() / 30;
        for (var offset = -1; offset <= 1; offset++)
            if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code),
                System.Text.Encoding.ASCII.GetBytes(Totp(Convert.FromHexString(secret), step + offset))))
                return step + offset;
        return null;
    }

    internal static string Totp(byte[] secret, long step, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 15;
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % (uint)Math.Pow(10, digits)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    private static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new System.Text.StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) { bits -= 5; result.Append(alphabet[(buffer >> bits) & 31]); }
        }
        if (bits > 0) result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    public X509Certificate2 Certificate()
    {
        var path = _path + ".certificate";
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12(
            Convert.FromBase64String(_protector.Unprotect(File.ReadAllText(path))), null);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=KittyClaw", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        foreach (var address in Addresses) names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = certificate.Export(X509ContentType.Pfx);
        File.WriteAllText(path, _protector.Protect(Convert.ToBase64String(pfx)));
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    private static IPAddress[] GetAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
        .Distinct().ToArray();

    private sealed record State
    {
        public string? Secret { get; init; }
        public long LastStep { get; init; } = -1;
        public string Generation { get; init; } = Guid.NewGuid().ToString("N");
        public Dictionary<string, DateTimeOffset> RevokedSessions { get; init; } = [];
    }
    private sealed record LoginAttempt(int Failures, DateTimeOffset BlockedUntil, DateTimeOffset LastAttempt);
    private sealed record Enrollment(string Secret, DateTimeOffset Expires);
    private sealed record Session(string Generation, DateTimeOffset Expires);
}
