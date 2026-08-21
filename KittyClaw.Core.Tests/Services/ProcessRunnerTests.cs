using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_ClosesInheritedPipesAfterParentExits()
    {
        if (!OperatingSystem.IsWindows()) return;

        var child = "Start-Sleep -Seconds 30";
        var childEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(child));
        var parent = $"Start-Process pwsh -NoNewWindow -ArgumentList '-NoProfile','-EncodedCommand','{childEncoded}'";
        var parentEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(parent));

        var run = ProcessRunner.RunAsync(
            "pwsh",
            $"-NoProfile -EncodedCommand {parentEncoded}",
            timeout: TimeSpan.FromSeconds(10));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Success, result.Stderr);
    }

    [Fact]
    public async Task RunAsync_TerminatesDetachedChildrenWhenParentExits()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), $"kittyclaw-process-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "orphan-survived.txt");
        try
        {
            var escapedMarker = marker.Replace("'", "''");
            var child = $"Start-Sleep -Seconds 2; Set-Content -LiteralPath '{escapedMarker}' -Value survived";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(child));
            var parent = $"Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-EncodedCommand','{encoded}'; Start-Sleep -Milliseconds 300";
            var parentEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(parent));

            var result = await ProcessRunner.RunAsync(
                "pwsh",
                $"-NoProfile -EncodedCommand {parentEncoded}",
                root,
                TimeSpan.FromSeconds(10));

            Assert.True(result.Success, result.Stderr);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(marker), "A detached child survived the ProcessRunner job boundary.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
