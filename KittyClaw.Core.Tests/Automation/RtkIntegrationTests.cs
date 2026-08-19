using System.Diagnostics;
using Microsoft.Data.Sqlite;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Automation;

public sealed class RtkIntegrationTests
{
    [Fact]
    public async Task DisabledProject_DoesNotProbeOrChangePromptAndEnvironment()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("rtk-disabled");
        var probeCalls = 0;
        var service = new RtkIntegrationService(projects, _ =>
        {
            probeCalls++;
            return Task.FromResult(new RtkIntegrationService.RtkProbeResult(true, "0.45.0", null));
        });

        var status = await service.GetStatusAsync(project.Slug);
        var prompt = RtkIntegrationService.AppendInstructions("original", status);
        var startInfo = new ProcessStartInfo("provider-cli");
        startInfo.Environment.Remove("RTK_TELEMETRY_DISABLED");
        RtkIntegrationService.ApplyEnvironment(startInfo, status);

        Assert.NotNull(status);
        Assert.False(status.Enabled);
        Assert.Equal(0, probeCalls);
        Assert.Equal("original", prompt);
        Assert.False(startInfo.Environment.ContainsKey("RTK_TELEMETRY_DISABLED"));
    }

    [Fact]
    public async Task EnabledAndAvailable_AddsSafeInstructionsAndDisablesTelemetry()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("rtk-enabled");
        await projects.SaveRtkEnabledAsync(project.Slug, true);
        var service = new RtkIntegrationService(projects, _ =>
            Task.FromResult(new RtkIntegrationService.RtkProbeResult(true, "0.45.0", null)));

        var status = await service.GetStatusAsync(project.Slug);
        var prompt = RtkIntegrationService.AppendInstructions("original", status);
        var startInfo = new ProcessStartInfo("provider-cli");
        RtkIntegrationService.ApplyEnvironment(startInfo, status);

        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.True(status.Available);
        Assert.Equal("instruction", status.Mode);
        Assert.Equal("0.45.0", status.Version);
        Assert.StartsWith("original", prompt);
        Assert.Contains("Never install RTK", prompt);
        Assert.Contains("rerun the original command unchanged", prompt);
        Assert.Equal("1", startInfo.Environment["RTK_TELEMETRY_DISABLED"]);
        Assert.Equal("provider-cli", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public async Task EnabledButUnavailable_FallsBackWithoutInstructionsAndStillDisablesTelemetry()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("rtk-missing");
        await projects.SaveRtkEnabledAsync(project.Slug, true);
        var service = new RtkIntegrationService(projects, _ =>
            Task.FromResult(new RtkIntegrationService.RtkProbeResult(false, null, "probe failed")));

        var status = await service.GetStatusAsync(project.Slug);
        var prompt = RtkIntegrationService.AppendInstructions("original", status);
        var startInfo = new ProcessStartInfo("provider-cli");
        RtkIntegrationService.ApplyEnvironment(startInfo, status);

        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.False(status.Available);
        Assert.Equal("unavailable", status.Mode);
        Assert.Equal("probe failed", status.Reason);
        Assert.Equal("original", prompt);
        Assert.Equal("1", startInfo.Environment["RTK_TELEMETRY_DISABLED"]);
    }

    [Fact]
    public async Task ExistingRegistry_MigratesWithRtkDisabled()
    {
        using var temp = new TempDir();
        var registryPath = Path.Combine(temp.Path, "registry.db");
        await using (var connection = new SqliteConnection($"Data Source={registryPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Projects (
                    Id INTEGER NOT NULL CONSTRAINT PK_Projects PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL, Slug TEXT NOT NULL, CreatedAt TEXT NOT NULL
                );
                INSERT INTO Projects (Name, Slug, CreatedAt)
                VALUES ('Legacy', 'legacy', '2026-01-01 00:00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var project = await new ProjectService(temp.Path).GetProjectAsync("legacy");

        Assert.NotNull(project);
        Assert.False(project.RtkEnabled);
    }
}
