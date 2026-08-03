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
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
