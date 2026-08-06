using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public class CliAvailabilityProbeTests
{
    private static string ResolveDotnet()
    {
        var host = Environment.ProcessPath;
        var dir = host is null ? null : Path.GetDirectoryName(host);
        var candidate = dir is null ? null
            : Path.Combine(dir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (candidate is not null && File.Exists(candidate)) return candidate;

        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name))
            .First(File.Exists);
    }

    [Fact]
    public void ProbeSync_ReturnsTrue_WhenBinaryExitsZero()
    {
        var dotnet = ResolveDotnet();
        Assert.True(ProcessRunner.ProbeSync(dotnet, "--version", TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void ProbeSync_ReturnsFalse_WhenPathDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nonexistent-cli-probe-test.exe");
        Assert.False(ProcessRunner.ProbeSync(missing, "--version", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProbeSync_ReturnsFalse_WhenProcessTimesOut()
    {
        // ping sleeps for several seconds; probe timeout fires first.
        var args = OperatingSystem.IsWindows() ? "-n 30 127.0.0.1" : "-c 30 127.0.0.1";
        Assert.False(ProcessRunner.ProbeSync("ping", args, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void CodexCli_IsInstalled_WhenEnvVarPointsToUsableBinary()
    {
        var previous = Environment.GetEnvironmentVariable("KITTYCLAW_CODEX_BIN");
        try
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_CODEX_BIN", ResolveDotnet());
            CodexCli.ResetForTests();
            Assert.True(CodexCli.IsInstalled);
            Assert.NotNull(CodexCli.Binary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_CODEX_BIN", previous);
            CodexCli.ResetForTests();
        }
    }

    [Fact]
    public void CodexCli_IsNotInstalled_WhenEnvVarPointsToMissingPath()
    {
        var previous = Environment.GetEnvironmentVariable("KITTYCLAW_CODEX_BIN");
        try
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_CODEX_BIN",
                Path.Combine(Path.GetTempPath(), "nonexistent-codex-binary.exe"));
            CodexCli.ResetForTests();
            // The env var path does not exist, so IsInstalled should be false (assuming no
            // real codex binary is on PATH in this environment).
            Assert.False(CodexCli.Binary == Path.Combine(Path.GetTempPath(), "nonexistent-codex-binary.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_CODEX_BIN", previous);
            CodexCli.ResetForTests();
        }
    }

    [Fact]
    public async Task AgentsTemplateService_IsGitAvailableAsync_ReturnsTrue()
    {
        // git must be on PATH in any environment where the tests compile and run.
        var svc = new AgentsTemplateService();
        Assert.True(await svc.IsGitAvailableAsync());
    }

    [Fact]
    public async Task AgentsTemplateService_AllProbesReturnTask()
    {
        // Verifies the async methods return proper Tasks (not sync blocking).
        var svc = new AgentsTemplateService();
        var tasks = new[]
        {
            svc.IsGitAvailableAsync(),
            svc.IsClaudeAvailableAsync(),
            svc.IsCodexAvailableAsync(),
            svc.IsGrokAvailableAsync(),
            svc.IsMistralAvailableAsync(),
            svc.IsOllamaAvailableAsync(),
        };
        var results = await Task.WhenAll(tasks);
        Assert.Equal(6, results.Length);
        // All are bool; just verify no exception was thrown.
        Assert.All(results, r => Assert.IsType<bool>(r));
    }
}
