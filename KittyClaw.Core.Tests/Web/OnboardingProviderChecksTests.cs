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

        Assert.Contains("IsClaudeAvailable", source);
        Assert.Contains("IsCodexAvailable", source);
        Assert.Contains("IsGrokAvailable", source);
        Assert.Contains("IsMistralAvailable", source);
        Assert.Contains("IsOllamaAvailable", source);
        Assert.Contains("OpenAI Codex", source);
        Assert.Contains("Grok Build", source);
        Assert.Contains("Mistral Vibe", source);
        Assert.Contains("Ollama", source);
    }

    [Fact]
    public void MissingOptionalProvider_DoesNotReopenOnboarding()
    {
        var source = RepoFile("KittyClaw.Web", "Components", "OnboardingGate.razor");

        Assert.Contains("!AppSettings.OnboardingSeen || !HasAgentProvider || !_gitInstalled", source);
        Assert.Contains("_claudeInstalled || _codexInstalled || _grokInstalled || _mistralInstalled", source);
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
