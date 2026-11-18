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
    /// <summary>GitHub Copilot CLI (`copilot`) — uses the user's signed-in Copilot subscription.</summary>
    Copilot,
    /// <summary>DeepSeek's Anthropic-compatible API, executed through the Claude Code CLI.</summary>
    DeepSeek,
}

/// <summary>
/// Single place that decides, from an effective model name, which CLI provider runs the
/// dispatch, which extra environment the subprocess needs, and whether the selection is
/// invalid. Used by column dispatchers, memory consolidation, and the chat endpoint.
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
    /// - null, claude-*, or claude:* → Claude CLI.
    /// - deepseek:*, copilot:*, codex:*, mistral:*, and grok-* → their provider CLI when installed.
    /// - any other model → Ollama through Claude CLI, requiring the project's LocalModelBaseUrl.
    /// </summary>
    public static Resolution Resolve(string? model, string? localModelBaseUrl)
    {
        if (model is null || model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            return new Resolution(CliProvider.Claude, null, null);

        if (model.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
            return new Resolution(CliProvider.Claude, null, null, model["claude:".Length..]);

        if (DeepSeekModelCatalog.IsDeepSeekModel(model))
        {
            var cliModel = DeepSeekModelCatalog.ToCliModel(model);
            return new Resolution(CliProvider.DeepSeek, new Dictionary<string, string>
            {
                ["ANTHROPIC_BASE_URL"] = "https://api.deepseek.com/anthropic",
                ["ANTHROPIC_MODEL"] = cliModel,
                ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "deepseek-v4-pro[1m]",
                ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = "deepseek-v4-pro[1m]",
                ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "deepseek-v4-flash",
                ["CLAUDE_CODE_SUBAGENT_MODEL"] = "deepseek-v4-flash",
                ["CLAUDE_CODE_EFFORT_LEVEL"] = "max",
                ["CLAUDE_CODE_AUTO_COMPACT_WINDOW"] = "786432",
            }, null, cliModel);
        }

        if (CopilotCli.IsCopilotModel(model))
        {
            if (!CopilotCli.IsInstalled)
                return new Resolution(CliProvider.Claude, null,
                    $"GitHub Copilot model '{model}': the Copilot CLI (copilot) was not found on this machine. " +
                    "Install it from https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli " +
                    "or point KITTYCLAW_COPILOT_BIN at the binary.");
            return new Resolution(CliProvider.Copilot, null, null, CopilotCli.ToCliModel(model));
        }

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
