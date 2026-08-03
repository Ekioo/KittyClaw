using System.Text.RegularExpressions;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public class BoardUnreadStateTests
{
    private static readonly string BoardPath = WebPath("Components", "Pages", "Board.razor");
    private static readonly string PanelPath = WebPath("Components", "TicketPanel.razor");

    [Fact]
    public void MovingC_ThenRecreatingState_PreservesUnreadAAndB_AndMarksOnlyCSeen()
    {
        var baseline = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var activity = baseline.AddMinutes(10);
        var viewed = new Dictionary<int, DateTime>();

        Assert.True(BoardUnreadState.IsUpdated(activity, 1, viewed, baseline));
        Assert.True(BoardUnreadState.IsUpdated(activity, 2, viewed, baseline));

        viewed[3] = activity.AddMinutes(1);
        var recreatedViewed = new Dictionary<int, DateTime>(viewed);

        Assert.True(BoardUnreadState.IsUpdated(activity, 1, recreatedViewed, baseline));
        Assert.True(BoardUnreadState.IsUpdated(activity, 2, recreatedViewed, baseline));
        Assert.False(BoardUnreadState.IsUpdated(activity, 3, recreatedViewed, baseline));
    }

    [Fact]
    public void PerTicketTimestamp_TakesPrecedenceOverNewerLegacyFallback()
    {
        var activity = new DateTime(2026, 1, 1, 10, 10, 0, DateTimeKind.Utc);
        var viewed = new Dictionary<int, DateTime> { [1] = activity.AddMinutes(-5) };

        Assert.True(BoardUnreadState.IsUpdated(activity, 1, viewed, activity.AddMinutes(5)));
    }

    [Fact]
    public void MutationRefreshes_DoNotAdvanceBoardWideFallback()
    {
        var source = File.ReadAllText(BoardPath);
        var refresh = ExtractMethod(source, "RefreshTicketsAsync");

        Assert.Contains("ListTicketsAsync", refresh);
        Assert.DoesNotContain("_lastVisitedAt =", refresh);
        Assert.DoesNotContain("board-last-visited", refresh);
        Assert.DoesNotContain("RefreshTicketsAndTouch", source);
    }

    [Fact]
    public void DragDrop_RefreshesAndMarksOnlyMovedTicket()
    {
        var drop = ExtractMethod(File.ReadAllText(BoardPath), "OnDropReorder");

        Assert.Contains("RefreshTicketsAsync()", drop);
        Assert.Contains("MarkTicketViewedAsync(ticketId)", drop);
    }

    [Fact]
    public void ExtractedPanel_ReportsOwnMutation_WhileParentUsesPureRefresh()
    {
        var board = File.ReadAllText(BoardPath);
        var panel = File.ReadAllText(PanelPath);

        Assert.Contains("OnMutatedTicket=\"MarkTicketViewedAsync\"", board);
        Assert.Contains("OnMutatedTicket.InvokeAsync(TicketId)", ExtractMethod(panel, "RefreshAsync"));
        Assert.Contains("RefreshTicketsAsync()", ExtractMethod(board, "OnPanelChanged"));
    }

    [Fact]
    public void Undo_UsesPureRefresh_AndAffectedTicketMetadata()
    {
        var source = File.ReadAllText(BoardPath);
        var undo = ExtractMethod(source, "PerformUndo");

        Assert.Contains("RefreshTicketsAsync()", undo);
        Assert.Contains("action.AffectedTicketId", undo);
        Assert.Contains("MarkTicketViewedAsync(ticketId)", undo);
        Assert.Contains("PushTicketUndo", source);
    }

    [Fact]
    public void DirectDeepLink_IsMarkedViewedAfterLocalStateLoads()
    {
        var afterRender = ExtractMethod(File.ReadAllText(BoardPath), "OnAfterRenderAsync");
        Assert.Contains("MarkTicketViewedAsync(openTicketId)", afterRender);
    }

    [Fact]
    public void PipelineTabs_ShowUnreadTicketCountWithAccessibleBadge()
    {
        var board = File.ReadAllText(BoardPath);
        var css = File.ReadAllText(WebPath("wwwroot", "app.css"));

        Assert.Contains("PipelineUnreadCount(pipeline.Id)", board);
        Assert.Contains("ticket.PipelineId == pipelineId && IsTicketUpdated(ticket)", board);
        Assert.Contains("pipeline-unread-badge", board);
        Assert.Contains("PipelineUnreadTickets", board);
        Assert.Contains(".pipeline-unread-badge", css);
        Assert.Contains("background: #f59e0b", css);
    }

    private static string WebPath(params string[] parts) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../KittyClaw.Web", Path.Combine(parts)));

    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source,
            $@"(?:private|protected|public)\s+(?:override\s+)?(?:async\s+)?(?:Task|void|bool)\s+{methodName}\b[\s\S]*?(?=\n\s{{4}}(?:private|protected|public)\s)");
        Assert.True(match.Success, $"{methodName} method not found");
        return match.Value;
    }
}
