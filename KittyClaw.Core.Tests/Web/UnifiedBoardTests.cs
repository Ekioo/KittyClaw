using System.Text.RegularExpressions;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Contract tests for the unified home (/): one page with two display modes (project cards /
/// unified kanban swimlanes), a single top bar (title + search + creation + view toggle),
/// per-column sort shared with the per-project board, safe return-navigation, and the
/// cross-lane drag rejection guarantees. Source-text guards mirror the pattern used by the
/// existing Board source-text tests.
/// Backported and evolved from GigaClaw 0c60ed9 / cc0bbc1 / PR #7 by @FoodBreakPedro (MIT).
/// </summary>
public class UnifiedBoardTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string WebPath(params string[] parts) =>
        Path.Combine([RepoRoot(), "KittyClaw.Web", .. parts]);

    private static string UnifiedBoardRazorPath() => WebPath("Components", "Pages", "UnifiedBoard.razor");
    private static string UnifiedBoardJsPath() => WebPath("wwwroot", "js", "unified-board.js");
    private static string LoadUnifiedBoard() => File.ReadAllText(UnifiedBoardRazorPath());

    // ── Routing: the unified home replaces the old project-list Home ─────────

    [Fact]
    public void UnifiedBoard_IsTheDefaultRoute()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("@page \"/\"", src);
        Assert.Contains("@page \"/board\"", src);
    }

    [Fact]
    public void OldHomePage_IsGone()
    {
        Assert.False(File.Exists(WebPath("Components", "Pages", "Home.razor")),
            "Home.razor must be removed — the unified home owns both display modes.");
    }

    // ── Single top bar: search + creation + view toggle in ONE header ────────

    [Fact]
    public void UnifiedBoard_SingleTopBar_HoldsSearchCreationAndViewToggle()
    {
        var src = LoadUnifiedBoard();
        var start = src.IndexOf("unified-page-header", StringComparison.Ordinal);
        var end = src.IndexOf("UnifiedBoardLoading", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "unified-page-header must precede the body");
        var header = src[start..end];
        Assert.Contains("UnifiedBoardSearchPlaceholder", header);
        Assert.Contains("<ProjectCreation", header);
        Assert.Contains("view-toggle", header);
        Assert.Contains("SetViewMode(\"kanban\")", header);
        Assert.Contains("SetViewMode(\"cards\")", header);
    }

    // ── Two display modes backed by extracted components ─────────────────────

    [Fact]
    public void UnifiedBoard_CardsMode_UsesProjectCardsComponent()
    {
        Assert.Contains("<ProjectCards", LoadUnifiedBoard());
        Assert.True(File.Exists(WebPath("Components", "ProjectCards.razor")));
    }

    [Fact]
    public void UnifiedBoard_PersistsViewModeChoice()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("unified-board-view", src);
    }

    [Fact]
    public void UnifiedBoard_MountsOnboardingGate()
    {
        Assert.Contains("<OnboardingGate />", LoadUnifiedBoard());
        Assert.True(File.Exists(WebPath("Components", "OnboardingGate.razor")));
        Assert.True(File.Exists(WebPath("Components", "ProjectCreation.razor")));
    }

    // ── Paused projects: bottom of the list, collapsed by default ────────────

    [Fact]
    public void UnifiedBoard_PausedLanes_SinkAndDefaultCollapsed()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains(".OrderBy(lane => lane.Project.IsPaused ? 1 : 0)", src);
        Assert.Contains("IsLaneCollapsed", src);
        Assert.Contains(": lane.Project.IsPaused", src);
    }

    [Fact]
    public void ProjectCards_PausedProjects_SinkToBottom()
    {
        var src = File.ReadAllText(WebPath("Components", "ProjectCards.razor"));
        Assert.Contains("p.IsPaused ? 1 : 0", src);
    }

    // ── Data loading and live updates ────────────────────────────────────────

    [Fact]
    public void UnifiedBoard_ListsAllProjects()
    {
        Assert.Contains("ProjectService.ListProjectsAsync()", LoadUnifiedBoard());
    }

    [Fact]
    public void UnifiedBoard_LoadsLanesConcurrently()
    {
        Assert.Contains("Task.WhenAll(laneTasks)", LoadUnifiedBoard());
    }

    [Fact]
    public void UnifiedBoard_SubscribesAndDisposesBoardUpdateNotifier()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("BoardUpdateNotifier.OnProjectUpdated += OnProjectUpdatedExternal", src);
        Assert.Contains("BoardUpdateNotifier.OnProjectUpdated -= OnProjectUpdatedExternal", src);
    }

    // ── Ticket interactions ──────────────────────────────────────────────────

    [Fact]
    public void UnifiedBoard_OpensTicketViaExistingBoardRoute()
    {
        // Clean URLs: no returnTo query — closing a route-opened ticket goes back through
        // browser history (see Board.razor _ticketOpenedFromRoute + NavigationHistory).
        var src = LoadUnifiedBoard();
        Assert.Contains("/board/{slug}/ticket/{ticketId}", src);
        Assert.DoesNotContain("returnTo", src);
    }

    [Fact]
    public void Board_ClosesHomeOpenedTickets_BackToHome()
    {
        // The unified home marks the origin in sessionStorage; Board consumes the flag on
        // close and goes back through browser history instead of forcing the board URL.
        var board = File.ReadAllText(WebPath("Components", "Pages", "Board.razor"));
        Assert.Contains("TryReturnToHomeAsync", board);
        Assert.Contains("kc-return-home", board);
        Assert.Contains("history.back", board);
        Assert.Contains("kc-return-home", LoadUnifiedBoard());
    }

    [Fact]
    public void UnifiedBoard_UsesReorderTicketAsyncForMoves()
    {
        Assert.Contains("TicketService.ReorderTicketAsync(", LoadUnifiedBoard());
    }

    [Fact]
    public void UnifiedBoard_LaneTicketCreation_UsesTicketService()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("OpenCreateTicket", src);
        Assert.Contains("TicketService.CreateTicketAsync(", src);
    }

    // ── Cross-lane drag rejection ────────────────────────────────────────────

    [Fact]
    public void UnifiedBoard_GatesDragOverAndDropOnSourceLane()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("_draggedFromSlug", src);
        Assert.Contains("@ondragover:preventDefault=\"@isSourceLane\"", src);
        Assert.Contains("@ondrop:preventDefault=\"@isSourceLane\"", src);
    }

    [Fact]
    public void UnifiedBoard_OnDrop_RejectsForeignLaneServerSide()
    {
        Assert.Contains("slug != _draggedFromSlug", LoadUnifiedBoard());
    }

    // ── Column sort shared with the per-project board ────────────────────────

    [Fact]
    public void UnifiedBoard_SharesColumnSortStateWithProjectBoard()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("BoardSortState SortState", src);
        Assert.Contains("board-sort-{slug}", src);
        Assert.Contains("OpenSortMenu", src);
        Assert.Contains("BoardSortState.ApplySort", src);
    }

    // ── Collapse persistence ─────────────────────────────────────────────────

    [Fact]
    public void UnifiedBoardJs_ExistsWithPerSlugStorage()
    {
        Assert.True(File.Exists(UnifiedBoardJsPath()), "KittyClaw.Web/wwwroot/js/unified-board.js must exist.");
        var js = File.ReadAllText(UnifiedBoardJsPath());
        Assert.Contains("unified-board-collapsed-", js);
        Assert.Contains("getCollapsed", js);
        Assert.Contains("setCollapsed", js);
        // Slugs without a stored value must be OMITTED so paused lanes can default collapsed.
        Assert.Contains("stored !== null", js);
    }

    [Fact]
    public void UnifiedBoard_UsesJsInteropForCollapseState()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("unifiedBoardStorage.getCollapsed", src);
        Assert.Contains("unifiedBoardStorage.setCollapsed", src);
    }

    [Fact]
    public void AppRazor_RegistersUnifiedBoardScript()
    {
        Assert.Contains("/js/unified-board.js", File.ReadAllText(WebPath("Components", "App.razor")));
    }

    // ── Localization ─────────────────────────────────────────────────────────

    [Fact]
    public void UnifiedBoardJson_EnAndFrKeysMatch()
    {
        var en = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "UnifiedBoard.en.json");
        var fr = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "UnifiedBoard.fr.json");
        Assert.True(File.Exists(en));
        Assert.True(File.Exists(fr));
        Assert.Equal(ExtractKeys(File.ReadAllText(en)), ExtractKeys(File.ReadAllText(fr)));
    }

    private static HashSet<string> ExtractKeys(string json) =>
        Regex.Matches(json, "\"([A-Za-z0-9]+)\"\\s*:").Select(m => m.Groups[1].Value).ToHashSet();
}
