namespace KittyClaw.Core.Tests.Web;

public sealed class BoardViewportLayoutTests
{
    [Fact]
    public void Board_uses_remaining_viewport_below_header_and_pipeline_tabs()
    {
        var root = RepoRoot();
        var board = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "Pages", "Board.razor"));
        var css = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "wwwroot", "app.css"));

        Assert.Contains("<div class=\"board-layout\">", board);
        Assert.Contains(".board-layout {", css);
        Assert.Contains("height: 100vh;", css);
        Assert.Contains("flex-direction: column;", css);
        Assert.Contains(".board-layout > .pipeline-tabs", css);
        Assert.Contains("flex: 1 1 auto;", css);
        Assert.Matches(@"(?s)\.board\s*\{[^}]*height:\s*auto;[^}]*flex:\s*1 1 auto;", css);
        Assert.Matches(@"(?s)\.board\s*\{[^}]*gap:\s*0;", css);
        Assert.Matches(@"(?s)\.column-insert-slot\s*\{[^}]*flex:\s*0 0 \.6rem;[^}]*min-width:\s*0;[^}]*margin:\s*0;", css);
        Assert.Matches(@"(?s)@if \(_columns\.Count == 0\)\s*\{\s*<button class=""add-column-lane""", board);
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
