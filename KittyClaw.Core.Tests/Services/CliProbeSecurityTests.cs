using System.Diagnostics;
using System.Text;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class CliProbeSecurityTests
{
    [Fact]
    public async Task Probe_does_not_wait_forever_for_a_descendant_holding_the_pipes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDir();
        var pidFile = Path.Combine(temp.Path, "child.pid");
        var childCommand = Encode("Start-Sleep -Seconds 30");
        var parent = $"$info = New-Object System.Diagnostics.ProcessStartInfo; " +
            "$info.FileName = 'powershell.exe'; $info.UseShellExecute = $false; $info.CreateNoWindow = $true; " +
            $"$info.Arguments = '-NoProfile -NonInteractive -EncodedCommand {childCommand}'; " +
            "$child = [System.Diagnostics.Process]::Start($info); " +
            $"[System.IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', [string]$child.Id + ':' + [string]$child.StartTime.ToUniversalTime().Ticks)";
        var probe = Task.Run(() => Probe(parent, "", TimeSpan.FromSeconds(3)));
        try
        {
            Assert.True(await probe.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(File.Exists(pidFile), "The synthetic parent never spawned its child.");
        }
        finally
        {
            // Kill only the fixture's child if the regression left it holding the pipes.
            if (File.Exists(pidFile))
            {
                var identity = (await File.ReadAllTextAsync(pidFile)).Split(':');
                if (identity.Length == 2 && int.TryParse(identity[0], out var pid)
                    && long.TryParse(identity[1], out var startTicks))
                {
                    try
                    {
                        using var child = Process.GetProcessById(pid);
                        if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == startTicks)
                            child.Kill(entireProcessTree: true);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
            await probe.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public async Task Probe_deadline_stops_a_hung_command()
    {
        var result = await Task.Run(() => Probe("Start-Sleep -Seconds 30", "sleep 30", TimeSpan.FromSeconds(1)))
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(result);
    }

    [Fact]
    public void Probe_rejects_a_nonzero_exit()
    {
        Assert.False(Probe("exit 17", "exit 17", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Probe_drains_large_newline_free_output_on_both_pipes()
    {
        var result = await Task.Run(() => Probe(
            "[Console]::Error.Write('e' * 8388608); [Console]::Out.Write('o' * 8388608)",
            "head -c 8388608 /dev/zero >&2; head -c 8388608 /dev/zero", TimeSpan.FromSeconds(10)))
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result);
    }

    [Fact]
    public async Task Synchronous_probe_does_not_require_the_callers_synchronization_context()
    {
        await Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            var context = new RejectingContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                Assert.True(Probe("[Console]::Out.Write('version')", "printf version", TimeSpan.FromSeconds(5)));
                Assert.Equal(0, context.Posts);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static bool Probe(string windows, string unix, TimeSpan timeout) =>
        OperatingSystem.IsWindows()
            ? ProcessRunner.ProbeSync("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Encode(windows), timeout)
            : ProcessRunner.ProbeSync("/bin/sh", "-c \"" + unix.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"", timeout);

    private static string Encode(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    private sealed class RejectingContext : SynchronizationContext
    {
        public int Posts { get; private set; }
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Posts++;
            throw new InvalidOperationException("The probe tried to use its blocked caller's context.");
        }
    }
}
