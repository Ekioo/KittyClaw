namespace KittyClaw.Core.Tests.Web;

public sealed class CircuitRecoveryContractTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void FatalCircuitError_ReloadsHealthyStalePageOnce()
    {
        var root = RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "Layout", "ReconnectModal.razor.js"));
        var layout = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("MutationObserver(recoverStaleCircuit)", script);
        Assert.Contains("/api/engine/health", script);
        Assert.Contains("Date.now() - lastReload < 60_000", script);
        Assert.Contains("location.reload()", script);
        Assert.Contains("L[\"SessionInterrupted\"]", layout);
        Assert.Contains("onclick=\"location.reload()\"", layout);
    }
}
