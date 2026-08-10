using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>Best-effort onboarding probes for Git and every external agent provider.</summary>
public sealed class AgentCliReadinessService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<string> _resolveClaude;
    private readonly Func<string?> _resolveCodex;
    private readonly Func<string?> _resolveGrok;
    private readonly Func<string?> _resolveMistral;
    private readonly Func<string, string, TimeSpan, Task<bool>> _probe;
    private readonly TimeSpan _probeTimeout;

    public AgentCliReadinessService()
        : this(
            () => ProcessLifecycleManager.ClaudeBinary,
            () => CodexCli.Binary,
            () => GrokCli.Binary,
            () => MistralCli.Binary,
            ProbeProcessAsync,
            ProbeTimeout)
    {
    }

    internal AgentCliReadinessService(
        Func<string> resolveClaude,
        Func<string?> resolveCodex,
        Func<string?> resolveGrok,
        Func<string?> resolveMistral,
        Func<string, string, TimeSpan, Task<bool>> probe,
        TimeSpan? probeTimeout = null)
    {
        _resolveClaude = resolveClaude;
        _resolveCodex = resolveCodex;
        _resolveGrok = resolveGrok;
        _resolveMistral = resolveMistral;
        _probe = probe;
        _probeTimeout = probeTimeout ?? ProbeTimeout;
    }

    public async Task<CliReadiness> ProbeAsync()
    {
        var git = ProbeSafelyAsync(() => "git", "--version");
        var claude = ProbeSafelyAsync(_resolveClaude, "--version");
        var codex = ProbeSafelyAsync(_resolveCodex, "--version");
        var grok = ProbeSafelyAsync(_resolveGrok, "--version");
        var mistral = ProbeSafelyAsync(_resolveMistral, "--version");
        var ollama = ProbeSafelyAsync(() => "ollama", "--version");
        await Task.WhenAll(git, claude, codex, grok, mistral, ollama);
        return new CliReadiness(git.Result, claude.Result, codex.Result, grok.Result, mistral.Result, ollama.Result);
    }

    private async Task<bool> ProbeSafelyAsync(Func<string?> resolve, string arguments)
    {
        try
        {
            var binary = await Task.Run(resolve).WaitAsync(_probeTimeout);
            return !string.IsNullOrWhiteSpace(binary)
                && await _probe(binary, arguments, _probeTimeout).WaitAsync(_probeTimeout);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeProcessAsync(string binary, string arguments, TimeSpan timeout)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(binary, arguments, null, timeout);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record CliReadiness(bool Git, bool Claude, bool Codex, bool Grok, bool Mistral, bool Ollama)
{
    public bool HasAgentProvider => Claude || Codex || Grok || Mistral;
    public bool Ready => Git && HasAgentProvider;
}
