using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Automation;

public sealed class RtkProbeProcessSecurityTests
{
    [Fact]
    public async Task Probe_drains_stderr_while_waiting_for_the_version_command()
    {
        var info = SyntheticProcessFixture.Command(
            "[Console]::Error.Write('e' * 48000); [Console]::Out.Write('rtk 1.2.3')",
            "head -c 48000 /dev/zero >&2; printf 'rtk 1.2.3'");
        var result = await RtkIntegrationService.ProbeBinaryAsync(info, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(result.Available, result.Reason);
        Assert.Equal("1.2.3", result.Version);
    }

    [Fact]
    public async Task Probe_cancellation_terminates_the_command_before_returning()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SyntheticProcessFixture();
        using var cancellation = new CancellationTokenSource();
        var info = SyntheticProcessFixture.Command(fixture.RecordSelf + "Start-Sleep -Seconds 30", "");
        var probe = RtkIntegrationService.ProbeBinaryAsync(info, cancellation.Token);
        using var child = await fixture.WaitForProcessAsync();
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
            Assert.True(child.WaitForExit(1000), "The cancelled RTK probe left its process running.");
        }
        finally { fixture.Stop(); }
    }

    [Fact]
    public async Task Probe_cannot_wait_forever_for_inherited_pipes_after_parent_exit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SyntheticProcessFixture();
        var info = SyntheticProcessFixture.Command(fixture.SpawnInheritedChild + "[Console]::Out.Write('rtk 1.2.3')", "");
        var probe = RtkIntegrationService.ProbeBinaryAsync(info, CancellationToken.None);
        try
        {
            var result = await probe.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(fixture.WasStarted, "The synthetic parent did not spawn its child.");
            Assert.True(result.Available, result.Reason);
            Assert.Equal("1.2.3", result.Version);
        }
        finally
        {
            fixture.Stop();
            await probe.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_bounds_captured_stdout_but_discards_large_stderr(bool stderr)
    {
        var info = SyntheticProcessFixture.Command(
            "[Console]::Out.Write('rtk 1.2.3'); [Console]::" + (stderr ? "Error" : "Out") + ".Write('synthetic-private-diagnostic' * 6000)",
            "printf 'rtk 1.2.3'; head -c 131072 /dev/zero" + (stderr ? " >&2" : ""));
        var result = await RtkIntegrationService.ProbeBinaryAsync(info, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(stderr, result.Available);
        if (stderr)
            Assert.Equal("1.2.3", result.Version);
        else
            Assert.Equal("RTK version probe returned excessive output.", result.Reason);
        Assert.DoesNotContain("synthetic-private", result.Reason ?? "");
    }

    [Fact]
    public async Task Probe_timeout_terminates_the_command()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new SyntheticProcessFixture();
        var info = SyntheticProcessFixture.Command(fixture.RecordSelf + "Start-Sleep -Seconds 30", "");
        var probe = RtkIntegrationService.ProbeBinaryAsync(info, CancellationToken.None);
        using var child = await fixture.WaitForProcessAsync();
        var result = await probe.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(result.Available);
        Assert.Equal("RTK version probe timed out.", result.Reason);
        Assert.True(child.WaitForExit(1000), "The timed-out RTK probe left its process running.");
    }

    [Fact]
    public async Task Precancelled_probe_never_starts_the_executable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RtkIntegrationService.ProbeBinaryAsync(
            new System.Diagnostics.ProcessStartInfo("kittyclaw-cancelled-rtk-must-not-start"), cancellation.Token));
    }

    [Fact]
    public async Task Probe_exit_failure_does_not_return_raw_diagnostics()
    {
        var info = SyntheticProcessFixture.Command(
            "[Console]::Error.Write('synthetic-private-diagnostic'); exit 17", "printf synthetic-private-diagnostic >&2; exit 17");
        var result = await RtkIntegrationService.ProbeBinaryAsync(info, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Equal("RTK version probe exited with code 17.", result.Reason);
    }

    [Fact]
    public async Task Probe_unrecognized_version_falls_back_without_enabling_RTK()
    {
        var info = SyntheticProcessFixture.Command("[Console]::Out.Write('unknown')", "printf unknown");
        var result = await RtkIntegrationService.ProbeBinaryAsync(info, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Null(result.Version);
        Assert.Contains("unrecognized version", result.Reason);
    }

}
