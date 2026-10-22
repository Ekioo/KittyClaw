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
        // The route carries the display mode: "/" and "/board" are the unified kanban,
        // "/projects" is the cards grid. Both modes stay bookmarkable, and the server
        // resolves the mode from the path with no client storage involved.
        var src = LoadUnifiedBoard();
        Assert.Contains("@page \"/\"", src);
        Assert.Contains("@page \"/board\"", src);
        Assert.Contains("@page \"/projects\"", src);
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
        // The toggle rewrites the URL in place and remembers the choice so the project
        // board's back link can point at the matching home route.
        Assert.Contains("history.pushState", src);
        Assert.Contains("unified-board-view", src);
        var board = File.ReadAllText(WebPath("Components", "Pages", "Board.razor"));
        Assert.Contains("_homeHref", board);
        Assert.Contains("unified-board-view", board);
    }

    [Fact]
    public void UnifiedBoard_ResolvesViewModeBeforeFirstRender()
    {
        // Returning to the home must paint the right mode immediately: the display mode is
        // derived from the route in OnInitializedAsync — available during prerender — so
        // neither a loading placeholder nor the wrong mode ever flashes, and cards mode
        // never pays for the kanban lanes.
        var src = LoadUnifiedBoard();
        var init = src.IndexOf("OnInitializedAsync", StringComparison.Ordinal);
        var afterRender = src.IndexOf("OnAfterRenderAsync", StringComparison.Ordinal);
        Assert.True(init >= 0 && afterRender > init, "OnInitializedAsync must precede OnAfterRenderAsync");
        var initBody = src[init..afterRender];
        Assert.Contains("\"/projects\"", initBody);
        Assert.Contains("ListProjectsAsync()", initBody);
    }

    [Fact]
    public void UnifiedBoard_StreamsItsShellWhileAllProjectLanesLoad()
    {
        var src = LoadUnifiedBoard();

        Assert.Contains("@attribute [StreamRendering]", src, StringComparison.Ordinal);
        var init = src.IndexOf("OnInitializedAsync", StringComparison.Ordinal);
        var afterRender = src.IndexOf("OnAfterRenderAsync", StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureLanesAsync", src[init..afterRender], StringComparison.Ordinal);
        Assert.Contains("_ = EnsureLanesAsync();", src[afterRender..], StringComparison.Ordinal);
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
    public void UnifiedBoard_LoadsLanesSequentially()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("foreach (var project in _projects)", src);
        Assert.Contains("_lanes.Add(await LoadLaneAsync(project))", src);
        Assert.DoesNotContain("Task.WhenAll(laneTasks)", src);
    }

    [Fact]
    public void UnifiedBoard_ShowsAndAppliesProjectPipelines()
    {
        var src = LoadUnifiedBoard();
        Assert.Contains("PipelineService PipelineService", src);
        Assert.Contains("pipeline-tabs--lane", src);
        Assert.Contains("SelectPipelineAsync(lane, pipeline.Id)", src);
        Assert.Contains("ticket.PipelineId == pipelineId", src);
        Assert.Contains("pipelineId: _newTicketPipelineId", src);
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
    public void UnifiedBoard_OpensTicketsInPlace_OverTheUnifiedView()
    {
        // Opening a ticket must NOT navigate to the project board: the extracted TicketPanel
        // renders over the unified view, and only the URL is updated (deep-linkable) through
        // history.pushState. Clean URLs: no returnTo query, no sessionStorage return flag.
        var src = LoadUnifiedBoard();
        Assert.Contains("<TicketPanel", src);
        Assert.Contains("history.pushState", src);
        Assert.Contains("/board/{slug}/ticket/{ticketId}", src);
        Assert.DoesNotContain("returnTo", src);
        Assert.DoesNotContain("kc-return-home", src);
    }

    [Fact]
    public void UnifiedBoard_TicketNumberSearch_RendersExactTicketAndParentResults()
    {
        var src = LoadUnifiedBoard();

        Assert.Contains("TicketNumberSearch.TryParse", src);
        Assert.Contains("TicketNumberSearch.Find(lane.Tickets, ticketId)", src);
        Assert.Contains("data-global-ticket-search-results", src);
        Assert.Contains("data-parent-result", src);
        Assert.Contains("OpenTicket(lane.Project.Slug, ticket.Id)", src);
    }

    [Fact]
    public void UnifiedBoard_TicketNumberSearch_LoadsTicketDataFromCardsMode()
    {
        var src = LoadUnifiedBoard();

        Assert.Contains("@bind:after=\"OnFilterChangedAsync\"", src);
        Assert.Contains("if (TicketNumberSearchId is not null)", src);
        Assert.Contains("await EnsureLanesAsync();", src);
    }

    [Fact]
    public void TicketPanel_IsExtracted_AndSharedWithTheProjectBoard()
    {
        // One ticket detail implementation for both views. The panel owns its own data and
        // escape handling; parents react through callbacks; the board wires its undo stack.
        Assert.True(File.Exists(WebPath("Components", "TicketPanel.razor")));
        var panel = File.ReadAllText(WebPath("Components", "TicketPanel.razor"));
        Assert.Contains("EventCallback OnClose", panel);
        Assert.Contains("EventCallback OnChanged", panel);
        Assert.Contains("EventCallback OnDeleted", panel);
        Assert.Contains("Action<string, Func<Task>>? PushUndo", panel);

        var board = File.ReadAllText(WebPath("Components", "Pages", "Board.razor"));
        Assert.Contains("<TicketPanel", board);
        // The sessionStorage return-home flag died with the navigation it compensated for.
        Assert.DoesNotContain("kc-return-home", board);
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
        Assert.Contains("@Assets[\"js/unified-board.js\"]", File.ReadAllText(WebPath("Components", "App.razor")));
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
