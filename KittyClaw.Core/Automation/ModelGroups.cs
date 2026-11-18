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
        var claudeInstalled = ClaudeCli.IsInstalled;
        return
        [
            new("Claude", ClaudeModelCatalog.Models, claudeInstalled,
                m => m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
                     || m.StartsWith("claude:", StringComparison.OrdinalIgnoreCase)),
            new("Grok", grok, GrokCli.IsInstalled, GrokCli.IsGrokModel),
            new("OpenAI Codex", codex, CodexCli.IsInstalled, CodexCli.IsCodexModel),
            new("Mistral Vibe", mistral, MistralCli.IsInstalled, MistralCli.IsMistralModel),
            new("DeepSeek", deepSeek, claudeInstalled, DeepSeekModelCatalog.IsDeepSeekModel),
            new("GitHub Copilot", copilot, CopilotCli.IsInstalled, CopilotCli.IsCopilotModel),
            new("Local (Ollama)", ollama),
        ];
    }
}
