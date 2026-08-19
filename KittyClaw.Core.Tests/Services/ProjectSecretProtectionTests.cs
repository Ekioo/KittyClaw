using System.Security.Cryptography;
using System.Text;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProjectSecretProtectionTests
{
    private sealed class InMemoryKeyStore : IProjectSecretKeyStore
    {
        public byte[]? Key;
        public int StoreCalls;
        public bool FailStore;
        public bool Locked;

        public byte[]? TryGetKey() => Locked
            ? throw new ProjectSecretProtectionUnavailableException("the key store is locked")
            : Key?.ToArray();

        public void StoreKey(byte[] key)
        {
            StoreCalls++;
            if (FailStore) throw new ProjectSecretProtectionUnavailableException("the key store rejected the key");
            Key = key.ToArray();
        }
    }

    private sealed class FakeSecretToolRunner : ISecretToolRunner
    {
        public readonly List<(string FileName, string[] Arguments, string? StandardInput)> Calls = [];
        public Func<string, IReadOnlyList<string>, string?, SecretToolResult> Handler =
            (_, _, _) => new SecretToolResult(0, "", "");

        public SecretToolResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput = null)
        {
            Calls.Add((fileName, arguments.ToArray(), standardInput));
            return Handler(fileName, arguments, standardInput);
        }
    }

    private static byte[] SampleKey() => Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray();

    [Fact]
    public void AesGcm_protector_round_trips_without_exposing_plaintext_and_randomizes_nonces()
    {
        var store = new InMemoryKeyStore();
        var protector = new AesGcmMasterKeyProtector(ProjectSecretProtectorIds.LinuxSecretService, "test store", store);
        var plaintext = Encoding.UTF8.GetBytes("""{"TOKEN":"cross-platform-value"}""");

        var first = protector.Protect(plaintext);
        var second = protector.Protect(plaintext);

        Assert.DoesNotContain("cross-platform-value", Encoding.UTF8.GetString(first));
        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, protector.Unprotect(first));
        Assert.Equal(plaintext, protector.Unprotect(second));
    }

    [Fact]
    public void AesGcm_protector_generates_and_persists_one_32_byte_master_key()
    {
        var store = new InMemoryKeyStore();
        var protector = new AesGcmMasterKeyProtector(ProjectSecretProtectorIds.MacOsKeychain, "test store", store);

        protector.Protect([1, 2, 3]);
        protector.Protect([4, 5, 6]);

        Assert.Equal(1, store.StoreCalls);
        Assert.Equal(32, store.Key!.Length);
    }

    [Fact]
    public void Tampered_payload_fails_closed_with_a_cryptographic_error()
    {
        var store = new InMemoryKeyStore();
        var protector = new AesGcmMasterKeyProtector(ProjectSecretProtectorIds.LinuxSecretService, "test store", store);
        var payload = protector.Protect(Encoding.UTF8.GetBytes("sensitive"));
        payload[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(payload));
    }

    [Fact]
    public void Unprotect_without_a_master_key_fails_closed()
    {
        var writer = new AesGcmMasterKeyProtector(
            ProjectSecretProtectorIds.LinuxSecretService, "test store", new InMemoryKeyStore());
        var payload = writer.Protect([1, 2, 3]);
        var reader = new AesGcmMasterKeyProtector(
            ProjectSecretProtectorIds.LinuxSecretService, "Linux Secret Service (libsecret)", new InMemoryKeyStore());

        var error = Assert.Throws<ProjectSecretProtectionUnavailableException>(() => reader.Unprotect(payload));
        Assert.Contains("master key", error.Message);
    }

    [Fact]
    public void Key_store_failures_block_protection_with_no_fallback()
    {
        var failing = new AesGcmMasterKeyProtector(
            ProjectSecretProtectorIds.MacOsKeychain, "test store", new InMemoryKeyStore { FailStore = true });
        Assert.Throws<ProjectSecretProtectionUnavailableException>(() => failing.Protect([1]));

        var locked = new AesGcmMasterKeyProtector(
            ProjectSecretProtectorIds.MacOsKeychain, "test store", new InMemoryKeyStore { Locked = true });
        Assert.Throws<ProjectSecretProtectionUnavailableException>(() => locked.Protect([1]));
        Assert.Throws<ProjectSecretProtectionUnavailableException>(() => locked.Unprotect(new byte[64]));
    }

    [Fact]
    public void MacOs_key_store_reads_an_existing_key()
    {
        var runner = new FakeSecretToolRunner
        {
            Handler = (_, args, _) => args[0] == "find-generic-password"
                ? new SecretToolResult(0, Convert.ToBase64String(SampleKey()) + "\n", "")
                : new SecretToolResult(1, "", "unexpected"),
        };

        Assert.Equal(SampleKey(), new MacOsKeychainKeyStore(runner).TryGetKey());
        var call = Assert.Single(runner.Calls);
        Assert.Equal("/usr/bin/security", call.FileName);
    }

    [Fact]
    public void MacOs_key_store_reports_a_missing_entry_as_null()
    {
        var runner = new FakeSecretToolRunner { Handler = (_, _, _) => new SecretToolResult(44, "", "") };
        Assert.Null(new MacOsKeychainKeyStore(runner).TryGetKey());
    }

    [Fact]
    public void MacOs_key_store_fails_closed_when_the_keychain_is_locked()
    {
        var runner = new FakeSecretToolRunner
        {
            Handler = (_, _, _) => new SecretToolResult(36, "", "security: SecKeychainSearchCopyNext: The user name or passphrase you entered is not correct."),
        };

        var error = Assert.Throws<ProjectSecretProtectionUnavailableException>(
            () => new MacOsKeychainKeyStore(runner).TryGetKey());
        Assert.Contains("Keychain", error.Message);
        Assert.Contains("unlock-keychain", error.Message);
    }

    [Fact]
    public void MacOs_key_store_fails_closed_when_the_security_tool_is_missing()
    {
        var runner = new FakeSecretToolRunner { Handler = (_, _, _) => new SecretToolResult(-1, "", "") { ToolMissing = true } };
        var error = Assert.Throws<ProjectSecretProtectionUnavailableException>(
            () => new MacOsKeychainKeyStore(runner).TryGetKey());
        Assert.Contains("security", error.Message);
    }

    [Fact]
    public void MacOs_key_store_passes_the_key_through_stdin_never_through_arguments()
    {
        var runner = new FakeSecretToolRunner();
        var key = SampleKey();

        new MacOsKeychainKeyStore(runner).StoreKey(key);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(new[] { "-i" }, call.Arguments);
        Assert.Contains(Convert.ToBase64String(key), call.StandardInput);
        Assert.DoesNotContain(call.Arguments, argument => argument.Contains(Convert.ToBase64String(key)));
    }

    [Fact]
    public void Linux_key_store_reads_an_existing_key()
    {
        var runner = new FakeSecretToolRunner
        {
            Handler = (_, args, _) => args[0] == "lookup"
                ? new SecretToolResult(0, Convert.ToBase64String(SampleKey()), "")
                : new SecretToolResult(1, "", "unexpected"),
        };

        Assert.Equal(SampleKey(), new LinuxSecretServiceKeyStore(runner).TryGetKey());
        Assert.Equal("secret-tool", Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    public void Linux_key_store_reports_a_missing_entry_as_null()
    {
        var runner = new FakeSecretToolRunner { Handler = (_, _, _) => new SecretToolResult(1, "", "") };
        Assert.Null(new LinuxSecretServiceKeyStore(runner).TryGetKey());
    }

    [Fact]
    public void Linux_key_store_fails_closed_when_the_secret_service_is_unreachable()
    {
        var runner = new FakeSecretToolRunner
        {
            Handler = (_, _, _) => new SecretToolResult(1, "", "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY"),
        };

        var error = Assert.Throws<ProjectSecretProtectionUnavailableException>(
            () => new LinuxSecretServiceKeyStore(runner).TryGetKey());
        Assert.Contains("Secret Service", error.Message);
        Assert.Contains("keyring", error.Message);
    }

    [Fact]
    public void Linux_key_store_fails_closed_when_secret_tool_is_missing()
    {
        var runner = new FakeSecretToolRunner { Handler = (_, _, _) => new SecretToolResult(-1, "", "") { ToolMissing = true } };
        var error = Assert.Throws<ProjectSecretProtectionUnavailableException>(
            () => new LinuxSecretServiceKeyStore(runner).TryGetKey());
        Assert.Contains("libsecret", error.Message);
    }

    [Fact]
    public void Linux_key_store_passes_the_key_through_stdin_and_fails_closed_on_store_errors()
    {
        var runner = new FakeSecretToolRunner();
        var key = SampleKey();

        new LinuxSecretServiceKeyStore(runner).StoreKey(key);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("store", call.Arguments[0]);
        Assert.Contains(Convert.ToBase64String(key), call.StandardInput);
        Assert.DoesNotContain(call.Arguments, argument => argument.Contains(Convert.ToBase64String(key)));

        runner.Handler = (_, _, _) => new SecretToolResult(1, "", "secret-tool: The keyring is locked");
        Assert.Throws<ProjectSecretProtectionUnavailableException>(
            () => new LinuxSecretServiceKeyStore(runner).StoreKey(key));
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("c2hvcnQ=")] // valid base64 but not 32 bytes
    public void Corrupt_master_key_entries_fail_closed(string storedValue)
    {
        var runner = new FakeSecretToolRunner { Handler = (_, _, _) => new SecretToolResult(0, storedValue, "") };
        var error = Assert.Throws<ProjectSecretProtectionUnavailableException>(
            () => new LinuxSecretServiceKeyStore(runner).TryGetKey());
        Assert.Contains("corrupt", error.Message);
    }

    [Fact]
    public void Factory_selects_the_native_protector_for_the_current_platform()
    {
        var protector = ProjectSecretProtectors.CreateForCurrentPlatform();
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsProjectSecretProtector>(protector);
            Assert.Equal(ProjectSecretProtectorIds.WindowsDpapi, protector.ProtectorId);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<AesGcmMasterKeyProtector>(protector);
            Assert.Equal(ProjectSecretProtectorIds.MacOsKeychain, protector.ProtectorId);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<AesGcmMasterKeyProtector>(protector);
            Assert.Equal(ProjectSecretProtectorIds.LinuxSecretService, protector.ProtectorId);
        }
        else
        {
            Assert.IsType<UnsupportedPlatformSecretProtector>(protector);
            Assert.Throws<PlatformNotSupportedException>(() => protector.Protect([1]));
        }
    }
}
