using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class JevConfigurationTests
{
    [Fact]
    public async Task Credentials_are_optional_encrypted_write_only_and_resolved_by_priority()
    {
        using var temp = new TempDir();
        var protector = new TestSecretProtector();
        var projects = new ProjectSecretVault(temp.Path, protector);
        string? environment = null;
        var service = new JevConfigurationService(projects, temp.Path, () => environment, protector);
        Assert.False((await service.GetAsync("alpha")).Available);
        Assert.Null(await service.ResolveKeyAsync("alpha", CancellationToken.None));
        environment = "environment-key";
        Assert.Equal("environment", (await service.GetAsync("alpha")).Source);
        Assert.Equal(environment, await service.ResolveKeyAsync("alpha", CancellationToken.None));
        const string saved = "global-private-api-key";
        await service.SaveKeyAsync(saved);
        var status = await service.GetAsync("alpha");
        Assert.True(status.ConfiguredInKittyClaw);
        Assert.Equal("kittyclaw", status.Source);
        Assert.DoesNotContain(saved, JsonSerializer.Serialize(status));
        Assert.Equal(saved, await service.ResolveKeyAsync("alpha", CancellationToken.None));
        Assert.Empty(await projects.ReadForInjectionAsync("alpha"));
        var file = Directory.GetFiles(Path.Combine(temp.Path, "integrations", "secrets")).Single();
        Assert.DoesNotContain(saved, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        var restarted = new JevConfigurationService(projects, temp.Path, () => environment, protector);
        Assert.Equal(saved, await restarted.ResolveKeyAsync("alpha", CancellationToken.None));
        await projects.SetAsync("alpha", JevQualificationService.SecretName, "project-key");
        Assert.Equal("project", (await restarted.GetAsync("alpha")).Source);
        Assert.Equal("project-key", await restarted.ResolveKeyAsync("alpha", CancellationToken.None));
        await projects.DeleteAsync("alpha", JevQualificationService.SecretName);
        await restarted.DeleteKeyAsync();
        Assert.Equal("environment", (await restarted.GetAsync("alpha")).Source);
        environment = null;
        Assert.False((await restarted.GetAsync("alpha")).Available);
    }

    [Fact]
    public async Task Blank_keys_are_rejected_and_locked_vaults_do_not_use_plaintext_fallback()
    {
        using var temp = new TempDir();
        var projects = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        var service = new JevConfigurationService(projects, temp.Path, () => null, new TestSecretProtector());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveKeyAsync("  "));
        await service.SaveKeyAsync("secret-to-protect");
        var locked = new JevConfigurationService(projects, temp.Path, () => "environment-key", new FailingSecretProtector());
        var status = await locked.GetAsync();
        Assert.False(status.Available);
        Assert.NotNull(status.Error);
        Assert.DoesNotContain("secret-to-protect", JsonSerializer.Serialize(status));
    }
}
