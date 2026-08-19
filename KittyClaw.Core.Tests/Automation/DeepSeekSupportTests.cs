using System.Diagnostics;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class DeepSeekSupportTests
{
    [Fact]
    public void Catalog_ContainsOnlyCurrentV4Models()
    {
        Assert.Equal(
        [
            "deepseek:deepseek-v4-pro[1m]",
            "deepseek:deepseek-v4-flash",
        ], DeepSeekModelCatalog.Models);
    }

    [Theory]
    [InlineData("deepseek:deepseek-v4-pro[1m]", "deepseek-v4-pro[1m]")]
    [InlineData("deepseek:deepseek-v4-flash", "deepseek-v4-flash")]
    public void Routing_UsesAnthropicCompatibleDeepSeekEnvironment(string selected, string resolved)
    {
        var target = ModelRouting.Resolve(selected, null).ToTarget(selected);

        Assert.Equal(CliProvider.DeepSeek, target.Provider);
        Assert.Equal(resolved, target.Model);
        Assert.Equal("https://api.deepseek.com/anthropic", target.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal(resolved, target.Environment["ANTHROPIC_MODEL"]);
        Assert.Equal("deepseek-v4-pro[1m]", target.Environment["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("deepseek-v4-flash", target.Environment["CLAUDE_CODE_SUBAGENT_MODEL"]);
        Assert.Equal("max", target.Environment["CLAUDE_CODE_EFFORT_LEVEL"]);
        Assert.False(target.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.Null(target.ValidationError);
    }

    [Fact]
    public async Task Backend_UsesClaudeCodeWithDedicatedSessionNamespace()
    {
        var context = new AgentRunContext
        {
            ProjectSlug = "p", WorkspacePath = "w", AgentName = "a",
            SkillFile = "a/SKILL.md", MaxTurns = 12,
            Target = ModelRouting.Resolve("deepseek:deepseek-v4-flash", null)
                .ToTarget("deepseek:deepseek-v4-flash"),
        };

        var invocation = await AgentCliBackend.For(CliProvider.DeepSeek)
            .BuildInvocationAsync(context, "prompt", "session-123", false, CancellationToken.None);

        Assert.Equal(ProcessLifecycleManager.ClaudeBinary, invocation.FileName);
        Assert.Contains("deepseek-v4-flash", invocation.Arguments);
        Assert.True(invocation.WritePromptToStdin);
        Assert.Equal("deepseek:chat:a", AgentRunner.SessionScopeKey("a", "chat", CliProvider.DeepSeek));
    }

    [Fact]
    public void Credentials_RequireProjectVaultKeyAndIgnoreInheritedAnthropicToken()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["ANTHROPIC_AUTH_TOKEN"] = "inherited-host-token";

        var ok = AgentRunner.ApplyProviderCredentials(
            startInfo, CliProvider.DeepSeek, new Dictionary<string, string>(), out var error);

        Assert.False(ok);
        Assert.Contains(DeepSeekModelCatalog.ApiKeySecretName, error);
        Assert.Equal("inherited-host-token", startInfo.Environment["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public void Credentials_MapProjectDeepSeekKeyOnlyForDeepSeekRuns()
    {
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DeepSeekModelCatalog.ApiKeySecretName] = "project-deepseek-key",
        };
        var deepSeek = new ProcessStartInfo();
        var claude = new ProcessStartInfo();

        Assert.True(AgentRunner.ApplyProviderCredentials(
            deepSeek, CliProvider.DeepSeek, secrets, out var deepSeekError));
        Assert.True(AgentRunner.ApplyProviderCredentials(
            claude, CliProvider.Claude, secrets, out var claudeError));

        Assert.Null(deepSeekError);
        Assert.Null(claudeError);
        Assert.Equal("project-deepseek-key", deepSeek.Environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.False(claude.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
    }
}
