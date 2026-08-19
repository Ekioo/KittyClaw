using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Services;

public sealed record ProjectSecretMetadata(string Name, DateTime UpdatedAt);

/// <summary>
/// Write-only project secret storage. The encrypted vault lives under KittyClaw's data directory,
/// never in the project workspace, and can only be decrypted through the platform-native protection
/// (Windows DPAPI, macOS Keychain, Linux Secret Service) of the account that wrote it.
/// </summary>
public sealed partial class ProjectSecretVault
{
    private const int CurrentVersion = 1;
    // On-disk envelope: magic (4) | format version (1) | protector id (1) | protector payload.
    // Vaults written by the #278 release are raw Windows DPAPI blobs without this header.
    private static ReadOnlySpan<byte> EnvelopeMagic => "KCSV"u8;
    private const byte EnvelopeFormatVersion = 2;
    private const int EnvelopeHeaderLength = 6;
    private readonly string _vaultDirectory;
    private readonly IProjectSecretProtector _protector;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ProjectSecretVault(string dataDirectory, IProjectSecretProtector? protector = null)
    {
        _vaultDirectory = Path.Combine(dataDirectory, "secrets");
        _protector = protector ?? ProjectSecretProtectors.CreateForCurrentPlatform();
    }

    public string VaultDirectory => _vaultDirectory;

    public async Task<IReadOnlyList<ProjectSecretMetadata>> ListAsync(string projectSlug, CancellationToken ct = default)
    {
        var document = await ReadDocumentAsync(projectSlug, ct);
        return document.Secrets
            .Select(pair => new ProjectSecretMetadata(pair.Key, pair.Value.UpdatedAt))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SetAsync(string projectSlug, string name, string value, CancellationToken ct = default)
    {
        ValidateSecretName(name);
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Secret value cannot be empty.", nameof(value));
        if (value.Length > 64 * 1024) throw new ArgumentException("Secret value is too large.", nameof(value));

        await WithLockAsync(projectSlug, async () =>
        {
            var document = await ReadDocumentUnlockedAsync(projectSlug, ct);
            document.Secrets[name] = new SecretEntry(value, DateTime.UtcNow);
            await WriteDocumentUnlockedAsync(projectSlug, document, ct);
        }, ct);
    }

    public async Task<bool> DeleteAsync(string projectSlug, string name, CancellationToken ct = default)
    {
        ValidateSecretName(name);
        var deleted = false;
        await WithLockAsync(projectSlug, async () =>
        {
            var document = await ReadDocumentUnlockedAsync(projectSlug, ct);
            deleted = document.Secrets.Remove(name);
            if (deleted) await WriteDocumentUnlockedAsync(projectSlug, document, ct);
        }, ct);
        return deleted;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadForInjectionAsync(
        string projectSlug, CancellationToken ct = default)
    {
        var document = await ReadDocumentAsync(projectSlug, ct);
        return document.Secrets.ToDictionary(
            pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task DeleteVaultAsync(string projectSlug, CancellationToken ct = default)
    {
        await WithLockAsync(projectSlug, () =>
        {
            var path = GetVaultPath(projectSlug);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }, ct);
    }

    private async Task<VaultDocument> ReadDocumentAsync(string projectSlug, CancellationToken ct)
    {
        VaultDocument? result = null;
        await WithLockAsync(projectSlug, async () => result = await ReadDocumentUnlockedAsync(projectSlug, ct), ct);
        return result!;
    }

    private async Task<VaultDocument> ReadDocumentUnlockedAsync(string projectSlug, CancellationToken ct)
    {
        var path = GetVaultPath(projectSlug);
        if (!File.Exists(path)) return new VaultDocument();
        var fileBytes = await File.ReadAllBytesAsync(path, ct);
        var plaintext = UnprotectFile(fileBytes);
        try
        {
            var document = JsonSerializer.Deserialize<VaultDocument>(plaintext)
                ?? throw new InvalidDataException("The project secret vault is empty or invalid.");
            if (document.Version != CurrentVersion)
                throw new InvalidDataException($"Unsupported project secret vault version {document.Version}.");
            document.Secrets = new Dictionary<string, SecretEntry>(document.Secrets, StringComparer.OrdinalIgnoreCase);
            return document;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task WriteDocumentUnlockedAsync(string projectSlug, VaultDocument document, CancellationToken ct)
    {
        Directory.CreateDirectory(_vaultDirectory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document);
        try
        {
            var payload = _protector.Protect(plaintext);
            var ciphertext = new byte[EnvelopeHeaderLength + payload.Length];
            EnvelopeMagic.CopyTo(ciphertext);
            ciphertext[4] = EnvelopeFormatVersion;
            ciphertext[5] = _protector.ProtectorId;
            payload.CopyTo(ciphertext, EnvelopeHeaderLength);
            var path = GetVaultPath(projectSlug);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, ciphertext, ct);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] UnprotectFile(byte[] fileBytes)
    {
        if (fileBytes.Length >= EnvelopeHeaderLength && fileBytes.AsSpan(0, EnvelopeMagic.Length).SequenceEqual(EnvelopeMagic))
        {
            var formatVersion = fileBytes[4];
            if (formatVersion != EnvelopeFormatVersion)
                throw new InvalidDataException(
                    $"Unsupported project secret vault format version {formatVersion}; " +
                    "this vault was written by a newer KittyClaw release.");
            var protectorId = fileBytes[5];
            if (protectorId != _protector.ProtectorId)
                throw new ProjectSecretProtectionUnavailableException(
                    $"This vault is protected with {DescribeProtector(protectorId)}, but this platform uses " +
                    $"{_protector.DisplayName}. Vault files are bound to the machine and account that wrote " +
                    "them; delete the vault and re-enter the secrets on this machine.");
            return _protector.Unprotect(fileBytes[EnvelopeHeaderLength..]);
        }

        // Headerless file: legacy #278 vault, always a raw Windows DPAPI blob.
        if (_protector.ProtectorId != ProjectSecretProtectorIds.WindowsDpapi)
            throw new ProjectSecretProtectionUnavailableException(
                "This vault was created by an earlier KittyClaw release on Windows (DPAPI) and cannot be " +
                $"opened with {_protector.DisplayName}. Open it on the original Windows account, or delete " +
                "the vault and re-enter the secrets on this machine.");
        return _protector.Unprotect(fileBytes);
    }

    private static string DescribeProtector(byte protectorId) => protectorId switch
    {
        ProjectSecretProtectorIds.WindowsDpapi => "Windows DPAPI (current user)",
        ProjectSecretProtectorIds.MacOsKeychain => "the macOS Keychain",
        ProjectSecretProtectorIds.LinuxSecretService => "the Linux Secret Service",
        _ => $"an unknown protection mechanism (id {protectorId})",
    };

    private string GetVaultPath(string projectSlug)
    {
        if (string.IsNullOrWhiteSpace(projectSlug) || !ProjectSlugRegex().IsMatch(projectSlug))
            throw new ArgumentException("Invalid project slug.", nameof(projectSlug));
        return Path.Combine(_vaultDirectory, projectSlug.ToLowerInvariant() + ".vault");
    }

    private static void ValidateSecretName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !SecretNameRegex().IsMatch(name))
            throw new ArgumentException(
                "Secret names must be valid environment variable names (letters, digits and underscores).",
                nameof(name));
    }

    private async Task WithLockAsync(string projectSlug, Func<Task> action, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(projectSlug, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { await action(); }
        finally { gate.Release(); }
    }

    private sealed class VaultDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, SecretEntry> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SecretEntry(string Value, DateTime UpdatedAt);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectSlugRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex SecretNameRegex();
}

public static class SecretRedactor
{
    public const string Replacement = "[REDACTED]";

    public static string Redact(string? text, IEnumerable<string> values)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = text;
        foreach (var value in values.Where(value => !string.IsNullOrEmpty(value)).Distinct().OrderByDescending(value => value.Length))
        {
            result = result.Replace(value, Replacement, StringComparison.Ordinal);
            var jsonEscaped = JsonSerializer.Serialize(value)[1..^1];
            if (!string.Equals(jsonEscaped, value, StringComparison.Ordinal))
                result = result.Replace(jsonEscaped, Replacement, StringComparison.Ordinal);
        }
        return result;
    }
}
