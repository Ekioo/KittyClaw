namespace KittyClaw.Core.Tests.Web;

public class OnboardingProviderChecksTests
{
    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.sln"))
               && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    [Fact]
    public void Onboarding_ChecksEverySupportedAgentProvider()
    {
        var source = RepoFile("KittyClaw.Web", "Components", "OnboardingGate.razor");

        Assert.Contains("@inject AgentCliReadinessService CliReadiness", source);
        Assert.Contains("_readiness.Claude", source);
        Assert.Contains("_readiness.Codex", source);
        Assert.Contains("_readiness.Grok", source);
        Assert.Contains("_readiness.Mistral", source);
        Assert.Contains("_readiness.Ollama", source);
        Assert.Contains("OpenAI Codex", source);
        Assert.Contains("Grok Build", source);
        Assert.Contains("Mistral Vibe", source);
        Assert.Contains("Ollama", source);
    }

    [Fact]
    public void MissingOptionalProvider_DoesNotReopenOnboarding()
    {
        var source = RepoFile("KittyClaw.Web", "Components", "OnboardingGate.razor");

        Assert.Contains("!AppSettings.OnboardingSeen || !_readiness.HasAgentProvider || !_readiness.Git", source);
        Assert.Contains("_readiness.HasAgentProvider", source);
        Assert.DoesNotContain("!_readiness.Claude || !_readiness.Codex", source);
        Assert.Contains("ProviderClass", source);
        Assert.Contains("optional", source);
    }

    [Fact]
    public void ProviderProbes_DoNotBlockTheInitialRender()
    {
        var source = RepoFile("KittyClaw.Web", "Components", "OnboardingGate.razor");

        Assert.Contains("protected override void OnInitialized()", source);
        Assert.Contains("if (firstRender && !_checkStarted)", source);
        Assert.Contains("_ = RecheckAndRefreshAsync();", source);
        Assert.DoesNotContain("OnInitializedAsync", source);
    }
}
