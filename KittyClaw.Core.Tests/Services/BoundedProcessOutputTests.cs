using System.Diagnostics;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class BoundedProcessOutputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_accepts_the_exact_character_limit(bool stderr)
    {
        var info = Output(stderr, 65536, wait: false);
        var result = await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(10), maxOutputCharacters: 65536);
        Assert.True(result.Success);
        Assert.Equal(new string('x', 65536), stderr ? result.Stderr : result.Stdout);
        Assert.Empty(stderr ? result.Stdout : result.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Excess_output_fails_promptly_even_when_the_producer_keeps_running(bool stderr)
    {
        var info = Output(stderr, 65537, wait: true);
        await Assert.ThrowsAsync<ProcessRunner.OutputLimitExceededException>(() =>
            ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(20), maxOutputCharacters: 65536)
                .WaitAsync(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task Invalid_limit_does_not_start_an_executable()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ProcessRunner.RunAsync(
            new ProcessStartInfo("kittyclaw-invalid-limit-must-not-start"), maxOutputCharacters: -1));
    }

    private static ProcessStartInfo Output(bool stderr, int characters, bool wait) => SyntheticProcessFixture.Command(
        "[Console]::" + (stderr ? "Error" : "Out") + $".Write('x' * {characters});" + (wait ? "Start-Sleep -Seconds 30" : ""),
        $"head -c {characters} /dev/zero | tr '\\000' x" + (stderr ? " >&2" : "") + (wait ? "; sleep 30" : ""));
}
