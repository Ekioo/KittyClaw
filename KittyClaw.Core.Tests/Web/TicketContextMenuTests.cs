using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class TicketContextMenuTests
{
    private static string RepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException();
    }

    [Fact]
    public void TicketCards_OpenAContextMenu_ThatCanMoveAcrossPipelines()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));

        Assert.Contains("OnContextMenu=\"@(e => OpenTicketMenuAsync(ticket, e))\"", source);
        Assert.Contains("TicketMenuMoveTo", source);
        Assert.Contains("ColumnService.ListColumnsAsync(Slug, pipeline.Id)", source);
        Assert.Contains("pipelineId: pipeline.Id, columnId: column.Id", source);
        Assert.Contains("PushUndo", source);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void TicketContextMenu_IsLocalized(string language)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Core", "Localization", $"Board.{language}.json")));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("TicketMenuOpen").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("TicketMenuMoveTo").GetString()));
    }
}
