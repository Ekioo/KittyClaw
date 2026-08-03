using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ConversationHandoffTests
{
    private static ChatMessageRow Message(int id, string role, string text) => new()
    {
        Id = id,
        TargetSlug = "agent",
        Role = role,
        Text = text,
        CreatedAt = DateTime.UtcNow.AddSeconds(id).ToString("o"),
    };

    [Fact]
    public void Build_IncludesConversationButExcludesToolNoise()
    {
        var history = new List<ChatMessageRow>
        {
            Message(1, "user", "Prépare un brief."),
            Message(2, "tool_use", "read_file"),
            Message(3, "assistant", "Je commence par le contexte."),
        };

        var handoff = ConversationHandoffBuilder.Build(
            history, "Grok", CliProvider.Codex);

        Assert.NotNull(handoff);
        Assert.Contains("from Grok to Codex", handoff);
        Assert.Contains("Owner: Prépare un brief.", handoff);
        Assert.Contains("Assistant: Je commence", handoff);
        Assert.DoesNotContain("read_file", handoff);
    }

    [Fact]
    public void Build_IsBoundedToRecentMessagesAndCharacters()
    {
        var history = Enumerable.Range(1, 20)
            .Select(id => Message(id, id % 2 == 0 ? "assistant" : "user",
                $"message-{id} " + new string('x', 2_000)))
            .ToList();

        var handoff = ConversationHandoffBuilder.Build(
            history, "Claude", CliProvider.Grok);

        Assert.NotNull(handoff);
        Assert.DoesNotContain("message-1 ", handoff);
        Assert.Contains("message-20 ", handoff);
        Assert.True(handoff.Length <= 12_100);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildPrompt_InjectsHandoffForNewAndResumedProviderSession(bool isResume)
    {
        var context = new AgentRunContext
        {
            ProjectSlug = "project",
            WorkspacePath = "workspace",
            AgentName = "agent",
            SkillFile = "(inline)",
            SessionScope = "chat",
            ExtraContext = "Et maintenant, conclus.",
            ConversationHandoff = "## Conversation handoff\nOwner: Le sujet initial.",
        };

        var prompt = await AgentRunner.BuildPromptAsync(
            context, "# Agent skill", isResume, CancellationToken.None);

        Assert.Contains("Conversation handoff", prompt);
        Assert.Contains("Le sujet initial", prompt);
        Assert.Contains("Et maintenant, conclus.", prompt);
    }

    [Fact]
    public void SessionRegistry_PersistsAndClearsLastChatProvider()
    {
        using var temp = new TempDir();
        var registry = new SessionRegistry();

        registry.SetLastChatProvider(temp.Path, "agent", "Grok");
        Assert.Equal("Grok", registry.GetLastChatProvider(temp.Path, "agent"));

        registry.SetLastChatProvider(temp.Path, "agent", "Codex");
        Assert.Equal("Codex", registry.GetLastChatProvider(temp.Path, "agent"));

        registry.ClearLastChatProvider(temp.Path, "agent");
        Assert.Null(registry.GetLastChatProvider(temp.Path, "agent"));
    }

    [Fact]
    public void SessionRegistry_PersistsAndClearsLastChatModel()
    {
        using var temp = new TempDir();
        var registry = new SessionRegistry();

        registry.SetLastChatModel(temp.Path, "agent", "gpt-5.6-sol");
        Assert.Equal("gpt-5.6-sol", registry.GetLastChatModel(temp.Path, "agent"));

        registry.ClearLastChatModel(temp.Path, "agent");
        Assert.Null(registry.GetLastChatModel(temp.Path, "agent"));
    }
}
