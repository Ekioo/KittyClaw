namespace KittyClaw.Core.Tests.Web;

public class ChatDrawerModelPersistenceTests
{
    private static string ChatDrawer() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "KittyClaw.Web", "Components", "ChatDrawer.razor"));

    [Fact]
    public void ModelSelection_IsPersistedPerProject_AndRestoredOnFirstRender()
    {
        var source = ChatDrawer();

        Assert.Contains("@bind:after=\"PersistSelectedModelAsync\"", source);
        Assert.Contains("$\"kc-chat-model-{ProjectSlug}\"", source);
        Assert.Contains("localStorage.setItem", source);
        Assert.Contains("localStorage.getItem", source);
    }

    [Fact]
    public void ExistingConversation_RestoresItsOwnModel_InsteadOfTheLastGlobalChoice()
    {
        var source = ChatDrawer();

        Assert.Contains("/chat/model?target=", source);
        Assert.Contains("_sessionModel = modelResponse.Model", source);
        Assert.Contains("if (_sessionModel is null)", source);
        Assert.Contains("await RestoreRememberedModelAsync()", source);
        Assert.Contains("_sessionModel = null", source);

        var endpoint = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "KittyClaw.Web", "Api", "Endpoints.Chat.cs"));
        Assert.Contains("LastCompletedForChatTarget(slug, target)?.Model", endpoint);
        Assert.Contains("chatHistory.Count > 0", endpoint);
    }

    [Fact]
    public void StoredModel_IsRestoredOnlyWhenNonEmptyAndAvailable()
    {
        var source = ChatDrawer();

        Assert.Contains("!string.IsNullOrWhiteSpace(model)", source);
        Assert.Contains("ClaudeModels.Contains(model)", source);
        Assert.Contains("_ollamaModels.Contains(model)", source);
        Assert.Contains("_grokModels.Contains(model)", source);
        Assert.Contains("_codexModels.Contains(model)", source);
        Assert.Contains("_mistralModels.Contains(model)", source);
    }

    [Fact]
    public void InterruptedConversation_IsAutomaticallyRestartedAndReattached()
    {
        var source = ChatDrawer();
        var endpoint = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "KittyClaw.Web", "Api", "Endpoints.Chat.cs"));
        var contracts = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "KittyClaw.Web", "Api", "Contracts.cs"));

        Assert.Contains("resp?.Interrupted == true", source);
        Assert.Contains("resumeInterrupted = true", source);
        Assert.Contains("LastInterruptedForChatTarget(slug, target)", endpoint);
        Assert.Contains("if (!req.ResumeInterrupted)", endpoint);
        Assert.Contains("bool ResumeInterrupted = false", contracts);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
