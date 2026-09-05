namespace KittyClaw.Core.Tests.Web;

public sealed class WorktreeSynchronizationPanelTests
{
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void TicketPanel_SeparatesIntegrationAndSynchronizationStates()
    {
        var panel = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "TicketPanel.razor"));

        Assert.Contains("data-testid=\"integration-status\"", panel);
        Assert.Contains("data-testid=\"synchronization-status\"", panel);
        Assert.Contains("data-testid=\"synchronization-lag\"", panel);
        Assert.Contains("data-testid=\"retry-synchronization\"", panel);
        Assert.Contains("SyncConflictFiles", panel);
        Assert.Contains("SyncTargetCommit", panel);
        Assert.Contains("RetrySynchronizationAsync", panel);
    }

    [Fact]
    public void RecoveryDocumentation_IsLinkedAndCoversEveryTerminalFailure()
    {
        var root = RepoRoot();
        var index = File.ReadAllText(Path.Combine(root, "doc", "index.md"));
        var recovery = File.ReadAllText(Path.Combine(root, "doc", "local-checkout-sync-recovery.md"));

        Assert.Contains("local-checkout-sync-recovery.md", index);
        Assert.Contains("Restore conflict", recovery);
        Assert.Contains("Diverged checkout", recovery);
        Assert.Contains("Concurrent local changes", recovery);
        Assert.Contains("Configured checkout is absent", recovery);
        Assert.Contains("syncBackupRef", recovery);
    }
}
