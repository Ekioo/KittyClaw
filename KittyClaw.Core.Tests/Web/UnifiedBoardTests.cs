using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Web;

public class UnifiedBoardTests
{
    private static string Page(string file) => File.ReadAllText(Path.Combine(
        AppCssHelper.FindRepoRoot(), "KittyClaw.Web", "Components", "Pages", file));

    [Theory]
    [InlineData("/")]
    [InlineData("/board")]
    [InlineData("/projects")]
    public void ExistingHomeBookmarks_OpenTheSameProjectDirectory(string route)
    {
        Assert.Contains($"@page \"{route}\"", Page("UnifiedBoard.razor"));
    }

    [Fact]
    public void Directory_DoesNotLoadOrRenderTickets()
    {
        var source = Page("UnifiedBoard.razor");
        Assert.Contains("<ProjectCards", source);
        Assert.DoesNotContain("TicketService", source);
        Assert.DoesNotContain("<TicketPanel", source);
        Assert.DoesNotContain("board-lane", source);
        Assert.DoesNotContain("SetViewMode", source);
        Assert.DoesNotContain("global-ticket-search-results", source);
    }

    [Fact]
    public void ProjectBoard_ReturnsToTheDirectoryRegardlessOfOldViewPreferences()
    {
        var source = Page("Board.razor");
        Assert.Contains("href=\"/\" class=\"back-link\"", source);
        Assert.DoesNotContain("unified-board-view", source);
    }

    [Fact]
    public void Directory_KeepsProjectCreationOnboardingAndLegalNotice()
    {
        var source = Page("UnifiedBoard.razor");
        Assert.Contains("<ProjectCreation", source);
        Assert.Contains("<OnboardingGate />", source);
        Assert.Contains("<TipOfTheDay />", source);
        Assert.Contains("AGPL-3.0-or-later", source);
    }

    [Fact]
    public void Directory_RefreshSubscriptionIsDisposed()
    {
        var source = Page("UnifiedBoard.razor");
        Assert.Contains("BoardUpdateNotifier.OnProjectUpdated += OnProjectUpdatedExternal", source);
        Assert.Contains("BoardUpdateNotifier.OnProjectUpdated -= OnProjectUpdatedExternal", source);
    }
}
