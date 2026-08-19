using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class OnboardingGateTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void Gate_displays_every_supported_provider_independently()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "OnboardingGate.razor"));

        Assert.Contains("_readiness.Claude", source);
        Assert.Contains("_readiness.Codex", source);
        Assert.Contains("_readiness.Grok", source);
        Assert.Contains("_readiness.Mistral", source);
        Assert.Contains("_readiness.Ollama", source);
        Assert.Contains("_readiness.Git", source);
        Assert.Contains("!_readiness.HasAgentProvider", source);
    }

    [Fact]
    public void Gate_reopens_only_when_git_or_every_agent_provider_is_unavailable()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "OnboardingGate.razor"));

        Assert.Contains("!AppSettings.OnboardingSeen || !_readiness.HasAgentProvider || !_readiness.Git", source);
        Assert.DoesNotContain("!_claudeInstalled", source);
    }

    [Fact]
    public void Onboarding_strings_are_consistent_across_supported_languages()
    {
        string[] languages = ["en", "fr", "es", "de", "it", "pt-BR", "ja"];
        var requiredKeys = new[]
        {
            "OnboardingClaudeGuidance", "OnboardingCodexGuidance", "OnboardingGrokGuidance",
            "OnboardingMistralGuidance", "OnboardingOllamaGuidance", "OnboardingGitGuidance",
        };

        foreach (var language in languages)
        {
            var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Home.{language}.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in requiredKeys)
                Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty(key).GetString()));
        }
    }
}
