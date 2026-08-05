namespace KittyClaw.Core.Automation;

/// <summary>Which CLI runs the agent subprocess for a dispatch.</summary>
public enum CliProvider
{
    /// <summary>The `claude` CLI — Anthropic cloud models, and Ollama local models via ANTHROPIC_BASE_URL.</summary>
    Claude,
    /// <summary>xAI's Grok Build CLI (`grok`) — used when a grok-* model is selected and the CLI is installed.</summary>
    Grok,
    /// <summary>OpenAI Codex CLI (`codex`) — used for explicitly qualified codex:* models.</summary>
    Codex,
    /// <summary>Mistral Vibe CLI (`vibe`) — used for explicitly qualified mistral:* models.</summary>
    Mistral,
}

/// <summary>
/// Single place that decides, from an effective model name, which CLI provider runs the
/// dispatch, which extra environment the subprocess needs, and whether the selection is
/// invalid. Used by both the automation dispatcher (ActionExecutor) and the chat endpoint.
/// </summary>
public static class ModelRouting
{
    public sealed record Resolution(
        CliProvider Provider,
        IReadOnlyDictionary<string, string>? ExtraEnv,
        string? Error,
        string? ResolvedModel = null)
    {
        public AgentDispatchTarget ToTarget(string? model, IReadOnlyDictionary<string, string>? baseEnvironment = null)
        {
            var env = baseEnvironment is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(baseEnvironment);
            if (ExtraEnv is not null)
                foreach (var pair in ExtraEnv) env[pair.Key] = pair.Value;
            return new AgentDispatchTarget(ResolvedModel ?? model, Provider, env, Error);
        }
    }

    /// <summary>
    /// Routing rules:
    /// - null, claude-*, or claude:* → claude CLI, no extra env.
    /// - grok-* → grok CLI when installed; otherwise an error (run fails without launching).
    /// - anything else → Ollama local model through the claude CLI: requires the project's
    ///   LocalModelBaseUrl and injects the ANTHROPIC_* env overrides.
    /// </summary>
    public static Resolution Resolve(string? model, string? localModelBaseUrl)
    {
        if (model is null || model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            return new Resolution(CliProvider.Claude, null, null);

        if (model.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
            return new Resolution(CliProvider.Claude, null, null, model["claude:".Length..]);

        if (CodexCli.IsCodexModel(model))
        {
            if (!CodexCli.IsInstalled)
                return new Resolution(CliProvider.Claude, null,
                    $"OpenAI model '{model}': the Codex CLI (codex) was not found on this machine. " +
                    "Install it from https://developers.openai.com/codex/cli or point KITTYCLAW_CODEX_BIN at the binary.");
            return new Resolution(CliProvider.Codex, null, null, CodexCli.ToCliModel(model));
        }

        if (MistralCli.IsMistralModel(model))
        {
            if (!MistralCli.IsInstalled)
                return new Resolution(CliProvider.Claude, null,
                    $"Mistral model '{model}': the Mistral Vibe CLI (vibe) was not found on this machine. " +
                    "Install it from https://docs.mistral.ai/vibe/code/cli/install-setup or point KITTYCLAW_MISTRAL_BIN at the binary.");
            var cliModel = MistralCli.ToCliModel(model);
            return new Resolution(CliProvider.Mistral, new Dictionary<string, string>
            {
                ["VIBE_ACTIVE_MODEL"] = cliModel,
            }, null, cliModel);
        }

        if (GrokCli.IsGrokModel(model))
        {
            if (!GrokCli.IsInstalled)
                return new Resolution(CliProvider.Claude, null,
                    $"Grok model '{model}': the Grok Build CLI (grok) was not found on this machine. " +
                    "Install it from https://x.ai/cli or point KITTYCLAW_GROK_BIN at the binary.");
            return new Resolution(CliProvider.Grok, null, null);
        }

        if (string.IsNullOrWhiteSpace(localModelBaseUrl))
            return new Resolution(CliProvider.Claude, null,
                $"Local model '{model}': LocalModelBaseUrl is not configured for this project");

        return new Resolution(CliProvider.Claude, new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = localModelBaseUrl.TrimEnd('/'),
            ["ANTHROPIC_AUTH_TOKEN"] = "ollama",
            ["ANTHROPIC_MODEL"] = model,
        }, null);
    }
}
