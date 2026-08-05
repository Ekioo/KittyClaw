using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Automation;

public class CliVersionProbeTests
{
    public static TheoryData<CliProvider> Providers => new()
    {
        CliProvider.Claude, CliProvider.Codex, CliProvider.Grok, CliProvider.Mistral,
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DetectsVersionForEveryExternalProvider(CliProvider provider)
    {
        var result = await CliVersionProbe.ProbeAsync(provider, "test-cli", null,
            (_, args, _) => Task.FromResult(Process(0, $"{provider} CLI 1.2.3", "", false, args)));

        Assert.Equal("1.2.3", result.Version);
        Assert.Equal("detected", result.Status);
        Assert.False(result.Mismatch);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ReportsConfiguredMismatchWithoutFailingProbe(CliProvider provider)
    {
        var result = await CliVersionProbe.ProbeAsync(provider, "test-cli", "2.0.0",
            (_, _, _) => Task.FromResult(Process(0, "version 1.2.3")));

        Assert.Equal("mismatch", result.Status);
        Assert.True(result.Mismatch);
        Assert.Equal("2.0.0", result.ExpectedVersion);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecordsProbeFailureForEveryExternalProvider(CliProvider provider)
    {
        var result = await CliVersionProbe.ProbeAsync(provider, "missing-cli", null,
            (_, _, _) => throw new FileNotFoundException("not found"));

        Assert.Equal("failed", result.Status);
        Assert.Contains("not found", result.Failure);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RecordsProbeTimeoutForEveryExternalProvider(CliProvider provider)
    {
        var result = await CliVersionProbe.ProbeAsync(provider, "slow-cli", null,
            (_, _, timeout) =>
            {
                Assert.Equal(CliVersionProbe.Timeout, timeout);
                return Task.FromResult(Process(null, "", "", true));
            });

        Assert.Equal("failed", result.Status);
        Assert.Equal("timeout", result.Failure);
    }

    [Fact]
    public async Task UnsupportedOutputIsObservable()
    {
        var result = await CliVersionProbe.ProbeAsync(CliProvider.Claude, "test-cli", null,
            (_, _, _) => Task.FromResult(Process(0, "unknown")));

        Assert.Equal("unsupported version output", result.Failure);
    }

    [Fact]
    public void RunLogStorePersistsCliVersionMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kittyclaw-cli-version-{Guid.NewGuid():N}");
        try
        {
            var store = new RunLogStore(directory);
            var run = new AgentRun
            {
                RunId = "version-run",
                ProjectSlug = "project",
                TicketId = 130,
                AgentName = "programmer",
                SkillFile = "programmer/SKILL.md",
                ConcurrencyGroup = "programmer",
                StartedAt = DateTime.UtcNow,
                CliVersion = new CliVersionMetadata("codex", "codex", "1.2.3", "1.2.3", "detected", null, false),
            };

            store.Save(run);

            var restored = Assert.Single(store.LoadAll());
            Assert.Equal(run.CliVersion, restored.CliVersion);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessRunner.ProcessResult Process(
        int? exitCode, string stdout, string stderr = "", bool timedOut = false, string? args = null)
    {
        if (args is not null) Assert.Equal("--version", args);
        return new ProcessRunner.ProcessResult(exitCode, stdout, stderr, timedOut);
    }
}
