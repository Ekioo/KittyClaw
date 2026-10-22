namespace KittyClaw.Core.Tests.Automation;

public sealed class CodexDesktopDetectionTests
{
    [Fact]
    public void DesktopInstallation_IsFoundOutsidePath_AndIncompleteUpdatesAreSkipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "kittyclaw-codex-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "incomplete"));
        try
        {
            var older = Path.Combine(root, "older", "codex.exe");
            var newer = Path.Combine(root, "newer", "codex.exe");
            foreach (var path in new[] { older, newer })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "synthetic executable fixture");
            }
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
            Assert.Equal(newer, CodexCli.ResolveDesktopBinary(root, _ => true));
            Assert.Equal(older, CodexCli.ResolveDesktopBinary(root, path => path != newer));
            Assert.Null(CodexCli.ResolveDesktopBinary(root, _ => false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingDesktopInstallation_DoesNotRunProbe()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Null(CodexCli.ResolveDesktopBinary(missing, _ => throw new InvalidOperationException()));
    }
}
