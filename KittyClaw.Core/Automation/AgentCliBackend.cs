using System.Text;
using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>A resolved model plus everything needed to dispatch it through one native CLI.</summary>
public sealed record AgentDispatchTarget(
    string? Model,
    CliProvider Provider,
    IReadOnlyDictionary<string, string> Environment,
    string? ValidationError = null)
{
    public static AgentDispatchTarget ClaudeDefault { get; } =
        new(null, CliProvider.Claude, new Dictionary<string, string>());
}

internal sealed record AgentCliInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool WritePromptToStdin,
    string? TemporaryFile = null,
    IReadOnlyList<string>? LogArguments = null);

/// <summary>
/// Provider-specific command construction, session semantics and stream normalization.
/// AgentRunner owns lifecycle/concurrency; backends own CLI differences.
/// </summary>
internal abstract class AgentCliBackend
{
    internal abstract CliProvider Provider { get; }
    internal abstract string SessionPrefix { get; }
    internal virtual bool CallerChoosesNewSessionId => true;

    internal abstract Task<AgentCliInvocation> BuildInvocationAsync(
        AgentRunContext context, string prompt, string sessionId, bool isResume,
        CancellationToken cancellationToken);

    internal virtual bool TryMapEvent(JsonElement root, string line, AgentRun run) => false;
    internal virtual string MapStderrKind(string line) => "stderr";

    internal static AgentCliBackend For(CliProvider provider) => provider switch
    {
        CliProvider.Grok => GrokBackend.Instance,
        CliProvider.Codex => CodexBackend.Instance,
        _ => ClaudeBackend.Instance,
    };

    private sealed class ClaudeBackend : AgentCliBackend
    {
        internal static readonly ClaudeBackend Instance = new();
        internal override CliProvider Provider => CliProvider.Claude;
        internal override string SessionPrefix => "";

        internal override Task<AgentCliInvocation> BuildInvocationAsync(
            AgentRunContext context, string prompt, string sessionId, bool isResume,
            CancellationToken cancellationToken)
        {
            var sessionName = context.TicketId is not null
                ? $"{context.AgentName} #{context.TicketId}"
                : context.AgentName;
            var args = new List<string>
            {
                "--print", "--verbose", "--output-format", "stream-json",
                "--dangerously-skip-permissions", "--max-turns", context.MaxTurns.ToString(),
            };
            if (isResume) { args.Add("--resume"); args.Add(sessionId); }
            else { args.Add("-n"); args.Add(sessionName); args.Add("--session-id"); args.Add(sessionId); }
            if (context.Target.Model is not null) { args.Add("--model"); args.Add(context.Target.Model); }
            return Task.FromResult(new AgentCliInvocation(
                ProcessLifecycleManager.ClaudeBinary, args, WritePromptToStdin: true));
        }
    }

    private sealed class GrokBackend : AgentCliBackend
    {
        internal static readonly GrokBackend Instance = new();
        internal override CliProvider Provider => CliProvider.Grok;
        internal override string SessionPrefix => "grok:";

        internal override async Task<AgentCliInvocation> BuildInvocationAsync(
            AgentRunContext context, string prompt, string sessionId, bool isResume,
            CancellationToken cancellationToken)
        {
            var args = new List<string>
            {
                "--output-format", "streaming-json", "--always-approve", "--no-auto-update",
                "--max-turns", context.MaxTurns.ToString(),
            };
            if (isResume) { args.Add("--resume"); args.Add(sessionId); }
            else { args.Add("--session-id"); args.Add(sessionId); }
            if (context.Target.Model is not null) { args.Add("--model"); args.Add(context.Target.Model); }

            var promptFile = Path.Combine(Path.GetTempPath(), $"kittyclaw-grok-prompt-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(promptFile, prompt, Encoding.UTF8, cancellationToken);
            args.Add("--prompt-file");
            args.Add(promptFile);
            var logArgs = args.SkipLast(1).Append("<prompt-file>").ToArray();
            return new AgentCliInvocation(
                GrokCli.Binary ?? "grok", args, WritePromptToStdin: false, promptFile, logArgs);
        }

        internal override bool TryMapEvent(JsonElement root, string line, AgentRun run) =>
            GrokStreamAdapter.TryMap(root, line, run);
    }

    private sealed class CodexBackend : AgentCliBackend
    {
        internal static readonly CodexBackend Instance = new();
        internal override CliProvider Provider => CliProvider.Codex;
        internal override string SessionPrefix => "codex:";
        internal override bool CallerChoosesNewSessionId => false;

        internal override Task<AgentCliInvocation> BuildInvocationAsync(
            AgentRunContext context, string prompt, string sessionId, bool isResume,
            CancellationToken cancellationToken)
        {
            var args = new List<string>
            {
                "exec", "--json", "--dangerously-bypass-approvals-and-sandbox",
                "--skip-git-repo-check", "--config", "notify=[]",
            };
            if (context.Target.Model is not null) { args.Add("--model"); args.Add(context.Target.Model); }
            if (context.ImagePaths is { Count: > 0 })
                foreach (var image in context.ImagePaths) { args.Add("--image"); args.Add(image); }
            if (isResume) { args.Add("resume"); args.Add(sessionId); }
            args.Add("-");
            return Task.FromResult(new AgentCliInvocation(
                CodexCli.Binary ?? "codex", args, WritePromptToStdin: true));
        }

        internal override bool TryMapEvent(JsonElement root, string line, AgentRun run) =>
            CodexStreamAdapter.TryMap(root, line, run);

        internal override string MapStderrKind(string line) =>
            line.Contains(" WARN ", StringComparison.Ordinal) ? "diagnostic" : "stderr";
    }
}
