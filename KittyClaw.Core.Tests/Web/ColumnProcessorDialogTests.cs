using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class ColumnProcessorDialogTests
{
    private static string RepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return directory!;
    }

    [Fact]
    public void Unsaved_processor_defaults_are_identified_as_a_draft()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ColumnProcessorDialog.razor"));

        Assert.Contains("if (!_processorExists)", source);
        Assert.Contains("ProcessorDraftNotice", source);
        Assert.Contains("_processorExists = processor is not null", source);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void Draft_notice_is_localized(string language)
    {
        var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Board.{language}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("ProcessorDraftNotice").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("ColumnProcessorActive").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            document.RootElement.GetProperty("ColumnProcessorInactive").GetString()));
    }

    [Fact]
    public void Boards_replace_the_decorative_dot_with_processor_state()
    {
        var board = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));
        var unified = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "UnifiedBoard.razor"));
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "app.css"));

        Assert.Contains("column-processor-indicator", board);
        Assert.Contains("_activeProcessorColumnIds.Contains(col.Id)", board);
        Assert.Contains("lane.ActiveProcessorColumnIds.Contains(col.Id)", unified);
        Assert.Contains("ProcessorService.ListAsync", board);
        Assert.Contains("ProcessorService.ListAsync", unified);
        Assert.DoesNotContain(".status-pill::before", css);
    }

    [Fact]
    public void Project_pipeline_tabs_are_left_aligned_and_vertically_balanced()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "app.css"));

        Assert.Contains("margin: .75rem 1rem;", css);
    }

    [Fact]
    public void Ticket_timeline_resolves_legacy_column_ids_to_processor_names()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "TicketPanel.razor"));

        Assert.Contains("ActivityAuthor(a.Author)", source);
        Assert.Contains("_processorNames.TryGetValue(columnId, out var processorName)", source);
    }

    [Fact]
    public void Column_engine_uses_processor_name_for_new_activity_authors()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Core", "Automation", "ColumnProcessingEngine.cs"));

        Assert.Contains("var activityAuthor = processor.Name", source);
        Assert.DoesNotContain("dispatch.Result, $\"column-{processor.ColumnId}\"", source);
    }
}
