namespace KittyClaw.Core.Tests.Web;

using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class ProviderGlobalSettingsUiTests
{
    [Fact]
    public void Global_settings_show_every_supported_provider_and_refresh_detection()
    {
        var source = File.ReadAllText(WebPath("Components", "Pages", "GlobalSettings.razor"));

        Assert.Contains("@inject AgentCliReadinessService CliReadiness", source);
        Assert.Contains("data-testid=\"agent-provider-settings\"", source);
        Assert.Contains("Claude Code", source);
        Assert.Contains("OpenAI Codex", source);
        Assert.Contains("Grok Build", source);
        Assert.Contains("Mistral Vibe", source);
        Assert.Contains("Ollama", source);
        Assert.Contains("DeepSeek V4", source);
        Assert.Contains("RefreshProvidersAsync", source);
        Assert.Contains("_ = RefreshProvidersAsync();", source);
    }

    [Fact]
    public void DeepSeek_card_explains_shared_cli_and_project_vault_configuration()
    {
        var source = File.ReadAllText(WebPath("Components", "Pages", "GlobalSettings.razor"));
        var english = File.ReadAllText(CorePath("Localization", "Common.en.json"));

        Assert.Contains("ProviderDeepSeekConfiguration", source);
        Assert.Contains("ProviderSharedClaudeCli", source);
        Assert.Contains("DEEPSEEK_API_KEY", english);
        Assert.Contains("project's vault", english);
    }

    [Fact]
    public void Provider_installation_documentation_covers_all_supported_clis()
    {
        var guide = File.ReadAllText(RepoPath("doc", "agent-providers.md"));
        var deepSeek = File.ReadAllText(RepoPath("doc", "deepseek.md"));

        foreach (var provider in new[] { "Claude Code", "OpenAI Codex", "Grok Build", "Mistral Vibe", "Ollama", "DeepSeek V4" })
            Assert.Contains(provider, guide);

        Assert.Contains("DEEPSEEK_API_KEY", deepSeek);
        Assert.Contains("Project settings → Secure vault", deepSeek);
        Assert.Contains("ANTHROPIC_BASE_URL=https://api.deepseek.com/anthropic", deepSeek);
    }

    [Fact]
    public void Global_settings_provider_text_is_localized_in_every_supported_language()
    {
        var source = File.ReadAllText(WebPath("Components", "Pages", "GlobalSettings.razor"));
        var keys = Regex.Matches(source, "L\\[\\\"((?:AgentProviders|Provider)[^\\\"]+)\\\"\\]")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var localizationDirectory = Path.GetDirectoryName(CorePath("Localization", "Common.en.json"))!;

        foreach (var file in Directory.GetFiles(localizationDirectory, "Common.*.json"))
        {
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            var missing = keys.Where(key => !translations.ContainsKey(key)).ToArray();
            Assert.True(missing.Length == 0, $"{Path.GetFileName(file)} is missing: {string.Join(", ", missing)}");
        }
    }

    private static string WebPath(params string[] parts) =>
        RepoPath(["KittyClaw.Web", .. parts]);

    private static string CorePath(params string[] parts) =>
        RepoPath(["KittyClaw.Core", .. parts]);

    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx"))
               && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
