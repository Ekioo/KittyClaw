using System.Collections.Concurrent;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>Availability of the `claude` CLI, which also carries DeepSeek and Ollama dispatches.</summary>
public static class ClaudeCli
{
    private static readonly ConcurrentDictionary<string, bool> Probed = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsInstalled =>
        Probed.GetOrAdd(ProcessLifecycleManager.ClaudeBinary,
            binary => ProcessRunner.ProbeSync(binary, "--version", TimeSpan.FromSeconds(5)));
}

/// <summary>One provider section of a model picker.</summary>
/// <param name="Owns">Tells whether a model name belongs to this provider, so a saved
/// selection of an uninstalled provider can still be shown under its own heading.</param>
public sealed record ModelGroup(
    string Label,
    IReadOnlyList<string> Models,
    bool Installed = true,
    Func<string, bool>? Owns = null)
{
    /// <summary>Uninstalled providers only appear to keep the currently selected model visible.</summary>
    public bool IsShown(string? selected) => Installed
        ? Models.Count > 0
        : !string.IsNullOrWhiteSpace(selected) && Owns?.Invoke(selected) == true;

    public IReadOnlyList<string> VisibleModels(string? selected) =>
        Installed ? Models : [selected!];
}

public static class ModelGroups
{
    /// <summary>Logo file name (under img/models) identifying the model vendor.</summary>
    public static string Icon(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("claude")) return "claude";
        if (m.Contains("gpt") || m.Contains("codex") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4")) return "openai";
        if (m.Contains("gemini")) return "gemini";
        if (m.Contains("grok")) return "grok";
        if (m.Contains("deepseek")) return "deepseek";
        if (m.Contains("mistral") || m.Contains("devstral")) return "mistral";
        if (m.Contains("mai-") || m.Contains("mai ")) return "microsoft";
        if (m.Contains("kimi")) return "kimi";
        if (m.StartsWith("copilot")) return "copilot";
        return "default";
    }
    /// <summary>Recommended model of the first installed provider, or null when none is available.</summary>
    public static string? Recommended(IEnumerable<ModelGroup> groups)
    {
        var group = groups.FirstOrDefault(g => g.Installed && g.Models.Count > 0);
        if (group is null) return null;
        return group.Models.Contains(ClaudeModelCatalog.DefaultModel) ? ClaudeModelCatalog.DefaultModel : group.Models[0];
    }

    /// <summary>Keeps <paramref name="current"/> when it is offered by an installed provider; otherwise falls back to the recommended model.</summary>
    public static string ResolveDefault(IReadOnlyList<ModelGroup> groups, string current) =>
        groups.Any(g => g.Installed && g.Models.Contains(current)) ? current : Recommended(groups) ?? current;

    public static bool IsListed(IEnumerable<ModelGroup> groups, string? selected) =>
        !string.IsNullOrWhiteSpace(selected)
        && groups.Any(g => g.IsShown(selected) && g.VisibleModels(selected).Contains(selected));

    public static IReadOnlyList<ModelGroup> Build(
        IReadOnlyList<string> grok,
        IReadOnlyList<string> codex,
        IReadOnlyList<string> mistral,
        IReadOnlyList<string> deepSeek,
        IReadOnlyList<string> copilot,
        IReadOnlyList<string> ollama)
    {
        // Debug aid: KITTYCLAW_FAKE_ALL_PROVIDERS=1 lists every provider (with sample models) so all logos can be reviewed.
        var fake = Environment.GetEnvironmentVariable("KITTYCLAW_FAKE_ALL_PROVIDERS") == "1";
        static IReadOnlyList<string> Or(bool fake, IReadOnlyList<string> real, params string[] sample) =>
            fake && real.Count == 0 ? sample : real;
        var claudeInstalled = fake || ClaudeCli.IsInstalled;
        return
        [
            new("Claude", ClaudeModelCatalog.Models, claudeInstalled,
                m => m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
                     || m.StartsWith("claude:", StringComparison.OrdinalIgnoreCase)),
            new("Grok", Or(fake, grok, "grok-4.5"), fake || GrokCli.IsInstalled, GrokCli.IsGrokModel),
            new("OpenAI Codex", Or(fake, codex, "gpt-5.3-codex"), fake || CodexCli.IsInstalled, CodexCli.IsCodexModel),
            new("Mistral Vibe", Or(fake, mistral, "devstral-2"), fake || MistralCli.IsInstalled, MistralCli.IsMistralModel),
            new("DeepSeek", Or(fake, deepSeek, "deepseek-v4"), claudeInstalled, DeepSeekModelCatalog.IsDeepSeekModel),
            new("GitHub Copilot", copilot, fake || CopilotCli.IsInstalled, CopilotCli.IsCopilotModel),
            new("Local (Ollama)", Or(fake, ollama, "llama3"), true),
        ];
    }
}