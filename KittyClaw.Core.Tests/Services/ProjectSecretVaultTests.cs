using System.Text;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProjectSecretVaultTests
{
    [Fact]
    public async Task Default_protector_is_native_per_platform_and_DPAPI_round_trips_on_Windows()
    {
        using var temp = new TempDir();
        var vault = new ProjectSecretVault(temp.Path);
        if (!OperatingSystem.IsWindows())
        {
            // On macOS/Linux the default protector goes through the native key store
            // (Keychain / Secret Service); selection and fail-closed behavior are
            // covered by ProjectSecretProtectionTests with fake key stores.
            Assert.IsNotType<WindowsProjectSecretProtector>(ProjectSecretProtectors.CreateForCurrentPlatform());
            return;
        }

        await vault.SetAsync("alpha", "TOKEN", "dpapi-roundtrip");
        Assert.Equal("dpapi-roundtrip", (await vault.ReadForInjectionAsync("alpha"))["TOKEN"]);
        Assert.DoesNotContain("dpapi-roundtrip",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(vault.VaultDirectory)))));
    }

    [Fact]
    public async Task Vault_encrypts_at_rest_and_only_lists_metadata()
    {
        using var temp = new TempDir();
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        const string value = "super-sensitive-value-278";

        await vault.SetAsync("alpha-project", "API_TOKEN", value);

        var metadata = Assert.Single(await vault.ListAsync("alpha-project"));
        Assert.Equal("API_TOKEN", metadata.Name);
        var vaultPath = Assert.Single(Directory.GetFiles(vault.VaultDirectory, "*.vault"));
        Assert.DoesNotContain(value, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(vaultPath)));
        Assert.Equal(value, (await vault.ReadForInjectionAsync("alpha-project"))["API_TOKEN"]);
    }

    [Fact]
    public async Task Projects_are_isolated_and_replacing_preserves_only_latest_value()
    {
        using var temp = new TempDir();
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync("alpha", "TOKEN", "first");
        await vault.SetAsync("alpha", "TOKEN", "second");
        await vault.SetAsync("beta", "TOKEN", "other-project");

        Assert.Equal("second", (await vault.ReadForInjectionAsync("alpha"))["TOKEN"]);
        Assert.Equal("other-project", (await vault.ReadForInjectionAsync("beta"))["TOKEN"]);
        Assert.True(await vault.DeleteAsync("alpha", "TOKEN"));
        Assert.Empty(await vault.ReadForInjectionAsync("alpha"));
        Assert.Equal("other-project", (await vault.ReadForInjectionAsync("beta"))["TOKEN"]);
    }

    [Fact]
    public async Task Deleting_project_removes_its_vault_before_slug_can_be_reused()
    {
        using var temp = new TempDir();
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        var projects = new ProjectService(temp.Path, vault);
        var project = await projects.CreateProjectAsync("Reusable secret project");
        await vault.SetAsync(project.Slug, "TOKEN", "old-value");

        Assert.True(await projects.DeleteProjectAsync(project.Slug));

        Assert.Empty(await vault.ListAsync(project.Slug));
    }

    [Fact]
    public async Task Protection_failure_never_writes_plaintext_fallback()
    {
        using var temp = new TempDir();
        var vault = new ProjectSecretVault(temp.Path, new FailingSecretProtector());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vault.SetAsync("alpha", "TOKEN", "must-not-touch-disk"));

        Assert.Empty(Directory.Exists(vault.VaultDirectory)
            ? Directory.GetFiles(vault.VaultDirectory)
            : []);
    }

    [Theory]
    [InlineData("BAD-NAME")]
    [InlineData("9STARTS_WITH_NUMBER")]
    [InlineData("")]
    public async Task Invalid_environment_variable_names_are_rejected(string name)
    {
        using var temp = new TempDir();
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => vault.SetAsync("alpha", name, "value"));
    }
}
