using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class SecretToolProcessSecurityTests
{
    [Fact]
    public async Task Standard_input_reaches_the_tool_without_entering_its_arguments()
    {
        const string value = "synthetic-key-material\n";
        var result = await Run("[Console]::Out.Write([Console]::In.ReadToEnd())", "cat",
            TimeSpan.FromSeconds(10), value);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(value, result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task Concurrent_pipe_drains_do_not_deadlock_on_stderr()
    {
        var result = await Run("[Console]::Error.Write('e' * 48000); [Console]::Out.Write('done')",
            "head -c 48000 /dev/zero >&2; printf done", TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", result.StandardOutput);
        Assert.Equal(48000, result.StandardError.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_covers_pipe_reads_and_blocked_standard_input(bool sendInput)
    {
        var run = Run("Start-Sleep -Seconds 20", "sleep 20", TimeSpan.FromSeconds(2),
            sendInput ? new string('s', 1024 * 1024) : null);
        var error = await Assert.ThrowsAsync<ProjectSecretProtectionUnavailableException>(() => run);
        Assert.Contains("did not answer", error.Message);
        Assert.DoesNotContain(new string('s', 100), error.Message);
    }

    [Fact]
    public async Task Excessive_tool_output_fails_closed_without_returning_it()
    {
        var error = await Assert.ThrowsAsync<ProjectSecretProtectionUnavailableException>(() =>
            Run("[Console]::Out.Write('secret' * 20000)", "head -c 120000 /dev/zero", TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain("secretsecret", error.Message);
    }

    private static Task<SecretToolResult> Run(string windowsScript, string unixScript, TimeSpan timeout, string? input = null)
    {
        var windows = OperatingSystem.IsWindows();
        var arguments = windows
            ? new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(windowsScript)) }
            : new[] { "-c", unixScript };
        return Task.Run(() => new ProcessSecretToolRunner(timeout).Run(windows ? "pwsh" : "/bin/sh", arguments, input))
            .WaitAsync(TimeSpan.FromSeconds(15));
    }
}
