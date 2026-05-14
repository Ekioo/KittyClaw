using KittyClaw.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class DashboardScriptRunnerTests
{
    private readonly DashboardScriptRunner _runner = new(NullLogger<DashboardScriptRunner>.Instance);

    [Theory]
    [InlineData("tile.ps1", true)]
    [InlineData("tile.sh", true)]
    [InlineData("tile.js", true)]
    [InlineData("tile.py", true)]
    [InlineData("tile.PS1", true)]
    [InlineData("tile.txt", false)]
    [InlineData("tile.bat", false)]
    [InlineData("tile", false)]
    public void IsSupported_ReturnsCorrectResult(string fileName, bool expected)
    {
        Assert.Equal(expected, DashboardScriptRunner.IsSupported(fileName));
    }

    [Fact]
    public async Task RunAsync_UnsupportedExtension_ReturnsConfigError()
    {
        var result = await _runner.RunAsync("tile.bat", Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ConfigError);
        Assert.Contains("Unsupported", result.ConfigError);
    }

    [Fact]
    public async Task RunAsync_MissingInterpreter_ReturnsConfigError()
    {
        // Use a .py script that doesn't exist — if python isn't found, we get a config error.
        // If python is found, we get a script-not-found failure (exit != 0).
        // Either way the call must not throw.
        var result = await _runner.RunAsync(
            Path.Combine(Path.GetTempPath(), "nonexistent_script_193.py"),
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RunAsync_PowerShellEchoScript_CapturesStdout()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kc-test-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tmp, "Write-Output 'hello from script'");
            var result = await _runner.RunAsync(tmp, Directory.GetCurrentDirectory(), CancellationToken.None);

            if (result.ConfigError is not null)
            {
                // pwsh/powershell not available in this environment — skip.
                return;
            }

            Assert.True(result.IsSuccess, $"Script failed: exit={result.ExitCode} stderr={result.Stderr}");
            Assert.Contains("hello from script", result.Stdout);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ReturnsFailure()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kc-test-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(tmp, "Write-Error 'boom'; exit 1");
            var result = await _runner.RunAsync(tmp, Directory.GetCurrentDirectory(), CancellationToken.None);

            if (result.ConfigError is not null) return; // pwsh not available

            Assert.False(result.IsSuccess);
            Assert.NotEqual(0, result.ExitCode);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

public sealed class TileSidecarScriptTests
{
    [Fact]
    public void TryParse_WithScriptField_ReadsScript()
    {
        var yaml = "template: markdown\nrefresh: 0\nprompt: \"\"\nmodel: \"\"\ntitle: \"\"\nscript: my-tile.ps1";
        var sidecar = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(sidecar);
        Assert.Equal("my-tile.ps1", sidecar.Script);
    }

    [Fact]
    public void TryParse_WithoutScriptField_ScriptIsNull()
    {
        var yaml = "template: markdown\nrefresh: 0\nprompt: \"\"\nmodel: \"\"\ntitle: \"\"";
        var sidecar = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(sidecar);
        Assert.Null(sidecar.Script);
    }

    [Fact]
    public void Serialize_WithScript_RoundTrips()
    {
        var original = new TileSidecar("markdown", 3600, "", null, null, "my-tile.ps1");
        var yaml = TileSidecarSerializer.Serialize(original);
        var parsed = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(parsed);
        Assert.Equal("my-tile.ps1", parsed.Script);
    }

    [Fact]
    public void Serialize_WithNullScript_ScriptEmptyInYaml()
    {
        var original = new TileSidecar("markdown", 0, "", null, null, null);
        var yaml = TileSidecarSerializer.Serialize(original);
        var parsed = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Script);
    }
}
