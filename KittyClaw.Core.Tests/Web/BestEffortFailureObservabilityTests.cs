namespace KittyClaw.Core.Tests.Web;

public sealed class BestEffortFailureObservabilityTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void RunDrawer_UserActionFailuresRemainVisibleAndRetryable()
    {
        var source = Read("KittyClaw.Web", "Components", "AgentRunDrawer.razor");

        Assert.Contains("role=\"alert\"", source);
        Assert.Contains("Impossible d’envoyer l’instruction. Réessayez.", source);
        Assert.Contains("Impossible d’arrêter l’exécution. Réessayez.", source);
        Assert.Contains("Impossible de relancer l’exécution. Réessayez.", source);
        Assert.Contains("if (!response.IsSuccessStatusCode)", source);
        Assert.Contains("finally { _retrying = false; }", source);
    }

    [Fact]
    public void OptionalPayloadsKeepTheirTolerantFallbacks()
    {
        var runDrawer = Read("KittyClaw.Web", "Components", "AgentRunDrawer.razor");
        var chatDrawer = Read("KittyClaw.Web", "Components", "ChatDrawer.razor");
        var runner = Read("KittyClaw.Core", "Automation", "AgentRunner.cs");

        Assert.Contains("catch (System.Text.Json.JsonException) { /* Optional preview payload", runDrawer);
        Assert.Contains("catch (System.Text.Json.JsonException) { return (\"\", []);", chatDrawer);
        Assert.Contains("catch (JsonException) { return false;", runner);
    }

    [Fact]
    public void RunnerCleanupUsesStructuredNonSensitiveDiagnostics()
    {
        var source = Read("KittyClaw.Core", "Automation", "AgentRunner.cs");

        Assert.Contains("TryKillProcess(proc, ctx, run, \"timeout cleanup\")", source);
        Assert.Contains("project {ProjectSlug} run {RunId} agent {AgentName} during {Operation}", source);
        Assert.Contains("catch (Exception ex) { _logger.LogDebug(ex, \"Failed to delete temporary image", source);
        Assert.DoesNotContain("{Prompt}", source);
        Assert.DoesNotContain("{ResponseBody}", source);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KittyClaw.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
