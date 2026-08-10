using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class AgentCliReadinessServiceTests
{
    public static TheoryData<bool, bool, bool, bool> IndependentProviders => new()
    {
        { true, false, false, false },
        { false, true, false, false },
        { false, false, true, false },
        { false, false, false, true },
        { true, true, true, true },
    };

    [Theory]
    [MemberData(nameof(IndependentProviders))]
    public async Task Probe_accepts_each_provider_independently_and_multiple_providers(
        bool claude, bool codex, bool grok, bool mistral)
    {
        var service = Create(claude, codex, grok, mistral, git: true);

        var result = await service.ProbeAsync();

        Assert.Equal(claude, result.Claude);
        Assert.Equal(codex, result.Codex);
        Assert.Equal(grok, result.Grok);
        Assert.Equal(mistral, result.Mistral);
        Assert.True(result.HasAgentProvider);
        Assert.True(result.Ready);
    }

    [Fact]
    public async Task Probe_reports_not_ready_when_no_provider_is_installed()
    {
        var result = await Create(false, false, false, false, git: true).ProbeAsync();

        Assert.False(result.HasAgentProvider);
        Assert.False(result.Ready);
    }

    [Fact]
    public async Task Probe_contains_failures_and_slow_probes()
    {
        var service = new AgentCliReadinessService(
            () => throw new InvalidOperationException("failed"),
            () => "codex",
            () => "grok",
            () => "vibe",
            async (binary, _, _) =>
            {
                if (binary == "codex") throw new TimeoutException();
                if (binary is "grok" or "vibe" or "ollama") await Task.Delay(TimeSpan.FromSeconds(1));
                return binary == "git";
            },
            TimeSpan.FromMilliseconds(20));

        var result = await service.ProbeAsync();

        Assert.True(result.Git);
        Assert.False(result.Claude);
        Assert.False(result.Codex);
        Assert.False(result.Grok);
        Assert.False(result.Mistral);
        Assert.False(result.Ollama);
        Assert.False(result.Ready);
    }

    private static AgentCliReadinessService Create(bool claude, bool codex, bool grok, bool mistral, bool git) =>
        new(
            () => "claude",
            () => "codex",
            () => "grok",
            () => "vibe",
            (binary, _, _) => Task.FromResult(binary switch
            {
                "git" => git,
                "claude" => claude,
                "codex" => codex,
                "grok" => grok,
                "vibe" => mistral,
                "ollama" => false,
                _ => false,
            }));
}
