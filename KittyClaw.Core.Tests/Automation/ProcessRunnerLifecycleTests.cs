using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ProcessRunnerLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kittyclaw-process-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cancellation_KillsDetachedWindowsDescendantBeforeReturning()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, "late-write.txt");
        var child = Path.Combine(_root, "child.ps1");
        await File.WriteAllTextAsync(child,
            $"Start-Sleep -Seconds 3; Set-Content -LiteralPath '{Escape(marker)}' -Value leaked");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -File \\\"{Escape(child)}\\\"'; Start-Sleep -Seconds 30\"",
            _root,
            TimeSpan.FromMinutes(1),
            ct: cancellation.Token));

        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(File.Exists(marker), "A detached descendant wrote after cancellation returned.");
    }

    [Fact]
    public async Task RepeatedCancellation_IsIdempotent()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
            "kittyclaw-cancelled-process-must-not-start",
            string.Empty,
            ct: cancellation.Token));
    }

    private static string Escape(string path) => path.Replace("'", "''");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
