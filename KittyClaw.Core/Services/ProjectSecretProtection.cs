using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace KittyClaw.Core.Services;

/// <summary>
/// Thrown when the native secret-protection mechanism (Windows DPAPI, macOS Keychain,
/// Linux Secret Service) is missing, locked, or otherwise unusable. The vault fails
/// closed: no plaintext fallback is ever attempted and no secret material leaks into
/// the message.
/// </summary>
public sealed class ProjectSecretProtectionUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>Stable identifiers persisted in the vault envelope header.</summary>
public static class ProjectSecretProtectorIds
{
    public const byte WindowsDpapi = 1;
    public const byte MacOsKeychain = 2;
    public const byte LinuxSecretService = 3;
}

public interface IProjectSecretProtector
{
    /// <summary>Identifier written to the vault envelope so readers can detect a protection mismatch.</summary>
    byte ProtectorId { get; }
    string DisplayName { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public static class ProjectSecretProtectors
{
    public static IProjectSecretProtector CreateForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsProjectSecretProtector()
        : OperatingSystem.IsMacOS() ? AesGcmMasterKeyProtector.ForMacOsKeychain()
        : OperatingSystem.IsLinux() ? AesGcmMasterKeyProtector.ForLinuxSecretService()
        : new UnsupportedPlatformSecretProtector();
}

public sealed class WindowsProjectSecretProtector : IProjectSecretProtector
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("KittyClaw.ProjectSecrets.v1"));

    public byte ProtectorId => ProjectSecretProtectorIds.WindowsDpapi;
    public string DisplayName => "Windows DPAPI (current user)";

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Windows DPAPI protection is only available on Windows; plaintext fallback is disabled.");
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Windows DPAPI protection is only available on Windows; plaintext fallback is disabled.");
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}

/// <summary>Fail-closed protector for platforms without a supported native secret store.</summary>
public sealed class UnsupportedPlatformSecretProtector : IProjectSecretProtector
{
    public byte ProtectorId => 0;
    public string DisplayName => "no supported protection";

    public byte[] Protect(byte[] plaintext) => throw Unsupported();
    public byte[] Unprotect(byte[] ciphertext) => throw Unsupported();

    private static PlatformNotSupportedException Unsupported() => new(
        "Project secrets require a native protection mechanism (Windows DPAPI, macOS Keychain or the " +
        "Linux Secret Service); this platform has none and plaintext fallback is disabled.");
}

/// <summary>Platform-native storage for the vault master key (macOS Keychain, Linux Secret Service).</summary>
public interface IProjectSecretKeyStore
{
    /// <summary>Returns the stored master key, or null when no key has been stored yet.
    /// Throws <see cref="ProjectSecretProtectionUnavailableException"/> when the store is locked or unusable.</summary>
    byte[]? TryGetKey();
    void StoreKey(byte[] key);
}

public sealed record SecretToolResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the native tool binary could not be found or started at all.</summary>
    public bool ToolMissing { get; init; }
}

/// <summary>Runs a native secret-store CLI (`security`, `secret-tool`); injectable for tests.</summary>
public interface ISecretToolRunner
{
    SecretToolResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput = null);
}

public sealed class ProcessSecretToolRunner : ISecretToolRunner
{
    private readonly TimeSpan _timeout;
    private const int MaxOutputCharacters = 64 * 1024;

    public ProcessSecretToolRunner() : this(TimeSpan.FromSeconds(30)) { }

    internal ProcessSecretToolRunner(TimeSpan timeout) => _timeout = timeout;

    public SecretToolResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput = null) =>
        RunAsync(fileName, arguments, standardInput).GetAwaiter().GetResult();

    private async Task<SecretToolResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new SecretToolResult(-1, "", "") { ToolMissing = true };
            using var job = KittyClaw.Core.Automation.ProcessJobObject.TryCreateAndAssign(process);
            using var deadline = new CancellationTokenSource(_timeout);
            try
            {
                // Start both drains before writing stdin: any of these three pipes can block.
                var stdout = DrainAsync(process.StandardOutput);
                var stderr = DrainAsync(process.StandardError);
                var input = WriteInputAsync();
                await Task.WhenAll(stdout, stderr, input, process.WaitForExitAsync(deadline.Token))
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                return new SecretToolResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));

                async Task WriteInputAsync()
                {
                    if (standardInput is null) return;
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }

                async Task<string> DrainAsync(StreamReader reader)
                {
                    try { return await ReadBoundedAsync(reader, deadline.Token).ConfigureAwait(false); }
                    catch
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw new ProjectSecretProtectionUnavailableException(
                    $"'{fileName}' did not answer within {_timeout.TotalSeconds:g} seconds; " +
                    "the native secret store may be waiting for an interactive unlock prompt.");
            }
            catch (IOException)
            {
                throw new ProjectSecretProtectionUnavailableException("Communication with the native secret store failed.");
            }
            finally
            {
                deadline.Cancel();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
        }
        catch (Win32Exception)
        {
            return new SecretToolResult(-1, "", "") { ToolMissing = true };
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return output.ToString();
            if (output.Length + read > MaxOutputCharacters)
                throw new ProjectSecretProtectionUnavailableException("The native secret store returned excessive output.");
            output.Append(buffer, 0, read);
        }
    }
}

/// <summary>Master key storage in the macOS login Keychain via the system `security` CLI.</summary>
public sealed class MacOsKeychainKeyStore(ISecretToolRunner? runner = null) : IProjectSecretKeyStore
{
    private const string SecurityBinary = "/usr/bin/security";
    private const string Service = "KittyClaw project secrets";
    private const string Account = "kittyclaw";
    private const int ItemNotFoundExitCode = 44; // errSecItemNotFound

    private readonly ISecretToolRunner _runner = runner ?? new ProcessSecretToolRunner();

    public byte[]? TryGetKey()
    {
        var result = _runner.Run(SecurityBinary, ["find-generic-password", "-s", Service, "-a", Account, "-w"]);
        if (result.ToolMissing) throw MissingTool();
        if (result.ExitCode == ItemNotFoundExitCode) return null;
        if (result.ExitCode != 0)
            throw new ProjectSecretProtectionUnavailableException(
                "The macOS Keychain refused access to the KittyClaw master key (the login keychain may be " +
                $"locked — run 'security unlock-keychain'). 'security' exited with code {result.ExitCode}.");
        return MasterKeyCodec.Decode(result.StandardOutput, "macOS Keychain");
    }

    public void StoreKey(byte[] key)
    {
        // `security -i` reads the command from stdin so the key never appears in a process argument list.
        var command = $"add-generic-password -U -s \"{Service}\" -a \"{Account}\" -w \"{Convert.ToBase64String(key)}\"\n";
        var result = _runner.Run(SecurityBinary, ["-i"], command);
        if (result.ToolMissing) throw MissingTool();
        if (result.ExitCode != 0)
            throw new ProjectSecretProtectionUnavailableException(
                "The macOS Keychain rejected the new KittyClaw master key (the login keychain may be locked). " +
                $"'security' exited with code {result.ExitCode}.");
    }

    private static ProjectSecretProtectionUnavailableException MissingTool() => new(
        $"The macOS '{SecurityBinary}' tool was not found; project secrets require the system Keychain " +
        "and plaintext fallback is disabled.");
}

/// <summary>Master key storage in the freedesktop Secret Service (D-Bus) via `secret-tool` from libsecret.</summary>
public sealed class LinuxSecretServiceKeyStore(ISecretToolRunner? runner = null) : IProjectSecretKeyStore
{
    private const string SecretToolBinary = "secret-tool";
    private const string ServiceAttribute = "kittyclaw.project-secrets";
    private const string AccountAttribute = "kittyclaw";

    private readonly ISecretToolRunner _runner = runner ?? new ProcessSecretToolRunner();

    public byte[]? TryGetKey()
    {
        var result = _runner.Run(SecretToolBinary, ["lookup", "service", ServiceAttribute, "account", AccountAttribute]);
        if (result.ToolMissing) throw MissingTool();
        if (result.ExitCode == 0) return MasterKeyCodec.Decode(result.StandardOutput, "Secret Service");
        // `secret-tool lookup` exits non-zero with an empty stderr when the item simply does not exist;
        // any diagnostic on stderr means the Secret Service itself is unreachable or locked.
        if (string.IsNullOrWhiteSpace(result.StandardError)) return null;
        throw ServiceUnavailable(result);
    }

    public void StoreKey(byte[] key)
    {
        var result = _runner.Run(
            SecretToolBinary,
            ["store", "--label", "KittyClaw project secrets", "service", ServiceAttribute, "account", AccountAttribute],
            Convert.ToBase64String(key) + "\n");
        if (result.ToolMissing) throw MissingTool();
        if (result.ExitCode != 0) throw ServiceUnavailable(result);
    }

    private static ProjectSecretProtectionUnavailableException MissingTool() => new(
        $"The '{SecretToolBinary}' command was not found; project secrets on Linux require libsecret " +
        "(package 'libsecret-tools' on Debian/Ubuntu, 'libsecret' on Fedora/Arch) and plaintext fallback is disabled.");

    private static ProjectSecretProtectionUnavailableException ServiceUnavailable(SecretToolResult result) => new(
        "The Linux Secret Service is unavailable or locked; ensure a D-Bus session bus and an unlocked " +
        "keyring daemon (e.g. gnome-keyring or KWallet) are running. " +
        $"'{SecretToolBinary}' exited with code {result.ExitCode}.");
}

internal static class MasterKeyCodec
{
    public const int KeySizeBytes = 32;

    public static byte[] Decode(string encoded, string storeName)
    {
        try
        {
            var key = Convert.FromBase64String(encoded.Trim());
            if (key.Length == KeySizeBytes) return key;
        }
        catch (FormatException)
        {
        }
        throw new ProjectSecretProtectionUnavailableException(
            $"The KittyClaw master key entry in the {storeName} is corrupt. Delete the 'KittyClaw project secrets' " +
            "entry and the vault files, then re-enter the secrets (the existing vault values cannot be recovered).");
    }
}

/// <summary>
/// AES-256-GCM protection keyed by a per-user master key held in a platform-native secret store.
/// Payload layout: nonce (12) | tag (16) | ciphertext.
/// </summary>
public sealed class AesGcmMasterKeyProtector : IProjectSecretProtector
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("KittyClaw.ProjectSecrets.v2");

    private readonly IProjectSecretKeyStore _keyStore;
    private readonly object _keyLock = new();
    private byte[]? _cachedKey;

    public AesGcmMasterKeyProtector(byte protectorId, string displayName, IProjectSecretKeyStore keyStore)
    {
        ProtectorId = protectorId;
        DisplayName = displayName;
        _keyStore = keyStore;
    }

    public static AesGcmMasterKeyProtector ForMacOsKeychain(ISecretToolRunner? runner = null) =>
        new(ProjectSecretProtectorIds.MacOsKeychain, "macOS Keychain", new MacOsKeychainKeyStore(runner));

    public static AesGcmMasterKeyProtector ForLinuxSecretService(ISecretToolRunner? runner = null) =>
        new(ProjectSecretProtectorIds.LinuxSecretService, "Linux Secret Service (libsecret)", new LinuxSecretServiceKeyStore(runner));

    public byte ProtectorId { get; }
    public string DisplayName { get; }

    public byte[] Protect(byte[] plaintext)
    {
        var key = GetKey(createIfMissing: true)!;
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

        var output = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, NonceSizeBytes);
        ciphertext.CopyTo(output, NonceSizeBytes + TagSizeBytes);
        return output;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceSizeBytes + TagSizeBytes)
            throw new InvalidDataException("The project secret vault payload is truncated.");
        var key = GetKey(createIfMissing: false)
            ?? throw new ProjectSecretProtectionUnavailableException(
                $"The KittyClaw master key was not found in the {DisplayName}; the vault cannot be decrypted. " +
                "If the secret store was reset, delete the vault files and re-enter the secrets.");

        var nonce = ciphertext.AsSpan(0, NonceSizeBytes);
        var tag = ciphertext.AsSpan(NonceSizeBytes, TagSizeBytes);
        var payload = ciphertext.AsSpan(NonceSizeBytes + TagSizeBytes);
        var plaintext = new byte[payload.Length];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, payload, tag, plaintext, AssociatedData);
        return plaintext;
    }

    private byte[]? GetKey(bool createIfMissing)
    {
        lock (_keyLock)
        {
            if (_cachedKey is not null) return _cachedKey;
            var key = _keyStore.TryGetKey();
            if (key is null && createIfMissing)
            {
                key = RandomNumberGenerator.GetBytes(MasterKeyCodec.KeySizeBytes);
                _keyStore.StoreKey(key);
            }
            return _cachedKey = key;
        }
    }
}
