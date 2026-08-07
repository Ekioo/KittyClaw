namespace KittyClaw.Core.Tests.Web;

public sealed class BoardRoutingDragVisualTests
{
    [Fact]
    public void Board_only_renders_ticket_drop_slots_for_allowed_routes()
    {
        var root = RepoRoot();
        var board = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "Pages", "Board.razor"));
        var css = KittyClaw.Core.Tests.Helpers.AppCssHelper.ReadAll(root);

        Assert.Contains("var ticketDropAllowed = IsTicketDropAllowed(col);", board);
        Assert.Contains("_draggedTicket is not null && ticketDropAllowed", board);
        Assert.Contains("@ondragover:preventDefault=\"@ticketDropAllowed\"", board);
        Assert.Contains("column-ticket-drop-allowed", board);
        Assert.Contains("column-ticket-drop-forbidden", board);
        Assert.Contains(".column.column-ticket-drop-allowed", css);
        Assert.Contains(".column.column-ticket-drop-forbidden", css);
    }

    private static string RepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return directory!;
    }
}
