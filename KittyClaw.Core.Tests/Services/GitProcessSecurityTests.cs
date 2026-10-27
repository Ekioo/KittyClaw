using System.Diagnostics;
using System.Text;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class GitProcessSecurityTests
{
    private static ProcessStartInfo Command(string windows, string unix)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh");
        var args = OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(windows)) }
            : new[] { "-c", unix };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    [Fact]
    public async Task Argument_list_preserves_literal_paths_and_shell_metacharacters()
    {
        using var temp = new TempDir();
        var windows = OperatingSystem.IsWindows();
        var file = Path.Combine(temp.Path, windows ? "echo.ps1" : "echo.sh");
        await File.WriteAllTextAsync(file, windows ? "param([string]$payload)\n[Console]::Out.Write($payload)" : "printf '%s' \"$1\"");
        var info = new ProcessStartInfo(windows ? "powershell.exe" : "/bin/sh");
        if (windows)
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" })
                info.ArgumentList.Add(arg);
        info.ArgumentList.Add(file);
        const string payload = "literal spaces & $() ; %COMSPEC% 'data'";
        info.ArgumentList.Add(payload);
        var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Stderr);
        Assert.Equal(payload, result.Stdout);
    }

    [Fact]
    public async Task Text_capture_drains_full_stderr_before_the_child_can_finish_stdout()
    {
        var info = Command("[Console]::Error.Write('e' * 131072); [Console]::Out.Write('done')",
            "head -c 131072 /dev/zero >&2; printf done");
        var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Success, result.Stderr);
        Assert.Equal("done", result.Stdout);
        Assert.Equal(131072, result.Stderr.Length);
    }

    [Fact]
    public async Task Binary_capture_preserves_every_byte_while_draining_stderr()
    {
        var octal = string.Concat(Enumerable.Range(0, 256).Select(i => "\\" + Convert.ToString(i, 8).PadLeft(3, '0')));
        var info = Command("[Console]::Error.Write('e' * 131072); $bytes = [byte[]](0..255); $s = [Console]::OpenStandardOutput(); for ($i=0; $i -lt 256; $i++) { $s.Write($bytes,0,256) }; $s.Flush()",
            "head -c 131072 /dev/zero >&2; i=0; while [ \"$i\" -lt 256 ]; do printf '" + octal + "'; i=$((i+1)); done");
        using var destination = new MemoryStream();
        var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(10), standardOutput: destination);
        Assert.True(result.Success, result.Stderr);
        Assert.Empty(result.Stdout);
        Assert.Equal(131072, result.Stderr.Length);
        Assert.Equal(Enumerable.Range(0, 65536).Select(i => (byte)(i % 256)).ToArray(), destination.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_bounds_hung_text_and_binary_processes(bool binary)
    {
        var info = Command("[Console]::Out.Write('ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 30", "printf ready; sleep 30");
        using var destination = new MemoryStream();
        var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(2), standardOutput: binary ? destination : null)
            .WaitAsync(TimeSpan.FromSeconds(9));
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task Deadline_still_applies_when_parent_exited_but_binary_transfer_is_blocked()
    {
        // Use a minimal child so shell startup cannot consume the transfer deadline.
        var windows = OperatingSystem.IsWindows();
        var info = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh");
        foreach (var arg in windows ? new[] { "/d", "/c", "echo", "payload" } : new[] { "-c", "printf payload" })
            info.ArgumentList.Add(arg);
        using var destination = new BlockedOutput();
        var execution = ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(2), standardOutput: destination);
        await destination.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(9));
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Failed_binary_write_cannot_be_reported_as_success()
    {
        var info = Command("[Console]::Out.Write('payload')", "printf payload");
        using var destination = new FailedOutput();
        await Assert.ThrowsAsync<IOException>(() => ProcessRunner.RunAsync(info,
            TimeSpan.FromSeconds(5), standardOutput: destination));
    }

    [Fact]
    public async Task Nonzero_exit_keeps_diagnostics_and_remains_a_failure()
    {
        var info = Command("[Console]::Out.Write('partial'); [Console]::Error.Write('failed'); exit 17", "printf partial; printf failed >&2; exit 17");
        var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(10));
        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal("partial", result.Stdout);
        Assert.Equal("failed", result.Stderr);
    }

    [Fact]
    public async Task Cancelled_call_never_starts_the_executable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
            new ProcessStartInfo("kittyclaw-cancelled-git-must-not-start"), ct: cancellation.Token));
    }

    private sealed class BlockedOutput : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
    }

    private sealed class FailedOutput : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromException(new IOException("Synthetic output-write failure."));
    }
}