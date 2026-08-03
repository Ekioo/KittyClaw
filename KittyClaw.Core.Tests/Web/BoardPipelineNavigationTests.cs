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
        Assert.Contains("Navigation.NavigateTo(BoardUrl(_subKanbanParentId, ticket.Id), replace: true);", source);
        Assert.Contains("Navigation.NavigateTo(BoardUrl(_subKanbanParentId, null), replace: true);", source);
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
