using KittyClaw.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Tests.Api;

public class TemporaryStoragePageTests
{
    [Fact]
    public async Task Page_RendersMeasuredProjectVolumeAndDisposableStorageWarning()
    {
        using var factory = new ProjectWorktreeSettingsHttpTests.ApiFactory();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
        });
        using var client = host.CreateClient();
        var projects = host.Services.GetRequiredService<ProjectService>();
        var project = await projects.CreateProjectAsync("Temporary storage page project");
        var folder = projects.PrepareRunWorkingFilesDirectory(project, "run-one");
        var proof = Path.Combine(folder, "temporary-proof");
        await File.WriteAllBytesAsync(proof, new byte[2048]);
        var response = await client.GetAsync("/temporary-storage");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Temporary storage page project", html);
        Assert.Contains("2 KiB", html);
        Assert.Contains("1 file", html);
        Assert.DoesNotContain("1 files", html);
        Assert.Contains("may be deleted at any time", html);
        Assert.Contains("No automatic cleanup", html);
        Assert.Contains("project-storage-usage", html);
        Assert.True(File.Exists(proof));
    }
}
