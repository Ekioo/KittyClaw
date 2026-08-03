namespace KittyClaw.Core.Tests.Web;

public class BoardPipelineNavigationTests
{
    private static string BoardSource() => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../KittyClaw.Web/Components/Pages/Board.razor")));

    [Fact]
    public void Ticket_navigation_keeps_the_selected_pipeline_in_the_url()
    {
        var source = BoardSource();

        Assert.Contains("[SupplyParameterFromQuery(Name = \"pipeline\")]", source);
        Assert.Contains("return _selectedPipelineId is int pipeline ? $\"{path}?pipeline={pipeline}\" : path;", source);
        Assert.Contains("await ReplaceTicketUrlAsync(ticket.Id);", source);
        Assert.Contains("await ReplaceTicketUrlAsync(null);", source);
        Assert.Contains("JS.InvokeVoidAsync(\"boardReplaceUrl\", BoardUrl(_subKanbanParentId, ticketId))", source);
    }

    [Fact]
    public void Ticket_panel_updates_history_without_rebuilding_the_board_component()
    {
        var source = BoardSource();
        var js = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../KittyClaw.Web/wwwroot/js/board.js")));

        Assert.Contains("window.boardReplaceUrl", js);
        Assert.Contains("history.replaceState(window.history.state", js);
        Assert.DoesNotContain("Navigation.NavigateTo(BoardUrl(_subKanbanParentId, ticket.Id)", source);
    }

    [Fact]
    public void Direct_legacy_ticket_link_infers_its_pipeline()
    {
        var source = BoardSource();

        Assert.Contains("PipelineId is null && TicketId is int ticketId", source);
        Assert.Contains("ticketPipelineId != _selectedPipelineId", source);
        Assert.Contains("_selectedPipelineId = ticketPipelineId;", source);
    }
}
