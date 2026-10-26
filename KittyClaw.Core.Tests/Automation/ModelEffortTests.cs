using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ModelEffortTests
{
    [Theory]
    [InlineData("ollama-model")]
    [InlineData("mistral:devstral")]
    [InlineData("claude-haiku-4-5")]
    [InlineData("codex:unknown")]
    public void UnknownCapabilities_DoNotOfferEffort(string model) =>
        Assert.Empty(ModelEffort.Options(model));

    [Theory]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("codex:gpt-6.1-sol")]
    public void MissingEffort_ResolvesToAnExplicitSupportedValue(string model)
    {
        Assert.Equal("medium", ModelEffort.Effective(model, null));
        Assert.Equal("medium", ModelEffort.Effective(model, ""));
        Assert.Equal("high", ModelEffort.Effective(model, "high"));
        Assert.Contains(ModelEffort.Initial(model), ModelEffort.Options(model));
    }

    [Fact]
    public void Capabilities_AreModelSpecific()
    {
        Assert.Contains("max", ModelEffort.Options("claude-sonnet-5-5"));
        Assert.DoesNotContain("xhigh", ModelEffort.Options("claude-opus-4-6"));
        Assert.Contains("ultra", ModelEffort.Options("codex:gpt-6.1-sol"));
        Assert.DoesNotContain("ultra", ModelEffort.Options("codex:gpt-6-luna"));
    }

    [Fact]
    public void ConversationEffort_SurvivesReloadAndCanReturnToDefault()
    {
        using var root = new KittyClaw.Core.Tests.Helpers.TempDir();
        var workspace = Path.Combine(root.Path, "workspace");
        var data = Path.Combine(root.Path, "data");
        var sessions = new SessionRegistry(data);
        sessions.SetLastChatEffort(workspace, "owner-chat", "high");
        var reloaded = new SessionRegistry(data);
        Assert.Equal("high", reloaded.GetLastChatEffort(workspace, "owner-chat"));
        Assert.Null(reloaded.GetLastChatEffort(workspace, "other-chat"));
        reloaded.SetLastChatEffort(workspace, "owner-chat", null);
        Assert.Null(new SessionRegistry(data).GetLastChatEffort(workspace, "owner-chat"));
    }

    [Theory]
    [InlineData(CliProvider.Claude, false)]
    [InlineData(CliProvider.Claude, true)]
    [InlineData(CliProvider.Codex, false)]
    [InlineData(CliProvider.Codex, true)]
    public async Task Effort_ReachesNativeCliOnNewAndResumedTurns(CliProvider provider, bool resume)
    {
        var context = new AgentRunContext
        {
            ProjectSlug = "p", WorkspacePath = "w", AgentName = "a", SkillFile = "a/SKILL.md",
            Target = new AgentDispatchTarget("model", provider, new Dictionary<string, string>(), Effort: "high"),
        };
        var invocation = await AgentCliBackend.For(provider)
            .BuildInvocationAsync(context, "prompt", "session", resume, CancellationToken.None);
        if (provider == CliProvider.Codex)
            Assert.Contains("model_reasoning_effort=\"high\"", invocation.Arguments);
        else
        {
            var args = invocation.Arguments.ToList();
            Assert.Equal("high", args[args.IndexOf("--effort") + 1]);
        }
    }
}
