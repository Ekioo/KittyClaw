using System.Text.RegularExpressions;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Tests for ticket #183: column scroll position preservation when opening/closing tickets.
/// All assertions are source-text based (RED on dev, GREEN after implementation).
/// </summary>
public class BoardScrollPreservationTests
{
    private static readonly string BoardRazorPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../KittyClaw.Web/Components/Pages/Board.razor"));

    private static readonly string BoardJsPath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../KittyClaw.Web/wwwroot/js/board.js"));

    // Case 1 & 2: SelectTicket saves scroll before navigating
    [Fact]
    public void SelectTicket_CallsSaveColumnScrollPositions_BeforeNavigate()
    {
        var src = File.ReadAllText(BoardRazorPath);

        // The method must call the JS function
        Assert.Contains("saveColumnScrollPositions", src);
        // It must appear inside SelectTicket (before the closing brace of the method)
        var selectTicketMatch = Regex.Match(src,
            @"private\s+async\s+Task\s+SelectTicket\s*\([^)]*\)\s*\{(?<body>[^}]*(?:\{[^}]*\}[^}]*)*)\}",
            RegexOptions.Singleline);
        Assert.True(selectTicketMatch.Success, "SelectTicket method not found");
        Assert.Contains("saveColumnScrollPositions", selectTicketMatch.Groups["body"].Value);
    }

    // Case 1 & 2: SelectTicket sets the restore flag
    [Fact]
    public void SelectTicket_Sets_RestoreScrollAfterRender_Flag()
    {
        var src = File.ReadAllText(BoardRazorPath);
        Assert.Contains("_restoreScrollAfterRender", src);

        var selectTicketMatch = Regex.Match(src,
            @"private\s+async\s+Task\s+SelectTicket\s*\([^)]*\)\s*\{(?<body>[^}]*(?:\{[^}]*\}[^}]*)*)\}",
            RegexOptions.Singleline);
        Assert.True(selectTicketMatch.Success, "SelectTicket method not found");
        Assert.Contains("_restoreScrollAfterRender = true", selectTicketMatch.Groups["body"].Value);
    }

    // Case 2 & 3: ClosePanel saves scroll positions
    [Fact]
    public void ClosePanel_CallsSaveColumnScrollPositions()
    {
        var src = File.ReadAllText(BoardRazorPath);

        // saveColumnScrollPositions must appear at least twice: once in SelectTicket, once in ClosePanel
        var count = Regex.Matches(src, @"saveColumnScrollPositions").Count;
        Assert.True(count >= 2,
            $"Expected saveColumnScrollPositions to appear at least 2 times (SelectTicket + ClosePanel), found {count}");
    }

    // Case 2 & 3: ClosePanel sets the restore flag
    [Fact]
    public void ClosePanel_Sets_RestoreScrollAfterRender_Flag()
    {
        var src = File.ReadAllText(BoardRazorPath);

        // _restoreScrollAfterRender = true must appear at least twice
        var count = Regex.Matches(src, @"_restoreScrollAfterRender\s*=\s*true").Count;
        Assert.True(count >= 2,
            $"Expected _restoreScrollAfterRender = true at least 2 times (SelectTicket + ClosePanel), found {count}");
    }

    // Case 1–4: OnAfterRenderAsync restores scroll positions using the flag
    [Fact]
    public void OnAfterRenderAsync_RestoresScrollPositions_WhenFlagSet()
    {
        var src = File.ReadAllText(BoardRazorPath);
        Assert.Contains("restoreColumnScrollPositions", src);
        Assert.Contains("_restoreScrollAfterRender", src);

        // The restore call must appear in OnAfterRenderAsync context
        // We check that OnAfterRenderAsync contains both the flag check and the restore call
        var afterRenderMatch = Regex.Match(src,
            @"OnAfterRenderAsync\s*\([^)]*\)(?<body>[\s\S]*?)(?=\n\s{4}(?:private|protected|public|async)\s)",
            RegexOptions.Singleline);
        Assert.True(afterRenderMatch.Success, "OnAfterRenderAsync not found in Board.razor");
        var body = afterRenderMatch.Groups["body"].Value;
        Assert.Contains("_restoreScrollAfterRender", body);
        Assert.Contains("restoreColumnScrollPositions", body);
    }

    // Case 4: Column div uses @key to ensure DOM node stability across renders
    [Fact]
    public void ColumnDiv_Has_KeyDirective_ForDomStability()
    {
        var src = File.ReadAllText(BoardRazorPath);

        // @key="col.Id" must appear on the column div
        Assert.Contains(@"@key=""col.Id""", src);
    }

    // Case 5 (edge): The restore flag is reset after use (no spurious re-restores)
    [Fact]
    public void RestoreScrollFlag_IsResetToFalse_AfterRestore()
    {
        var src = File.ReadAllText(BoardRazorPath);

        // The flag must be set to false somewhere (reset after use in OnAfterRenderAsync)
        Assert.Contains("_restoreScrollAfterRender = false", src);
    }

    // board.js must exist with the two required functions
    [Fact]
    public void BoardJs_Exists_WithSaveAndRestoreFunctions()
    {
        Assert.True(File.Exists(BoardJsPath),
            $"board.js not found at {BoardJsPath}");

        var js = File.ReadAllText(BoardJsPath);
        Assert.Contains("saveColumnScrollPositions", js);
        Assert.Contains("restoreColumnScrollPositions", js);
    }

    // board.js must use .column-body selector to target column scrollable areas
    [Fact]
    public void BoardJs_TargetsColumnBodyElements()
    {
        Assert.True(File.Exists(BoardJsPath),
            $"board.js not found at {BoardJsPath}");

        var js = File.ReadAllText(BoardJsPath);
        Assert.Contains(".column-body", js);
    }

    // board.js must save and restore scrollTop
    [Fact]
    public void BoardJs_SavesAndRestores_ScrollTop()
    {
        Assert.True(File.Exists(BoardJsPath),
            $"board.js not found at {BoardJsPath}");

        var js = File.ReadAllText(BoardJsPath);
        Assert.Contains("scrollTop", js);
    }

    // Board.razor must load board.js (via script tag)
    [Fact]
    public void Board_LoadsBoardJs_ScriptTag()
    {
        // board.js may be referenced in Board.razor or App.razor
        var boardSrc = File.ReadAllText(BoardRazorPath);
        var appRazorPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../KittyClaw.Web/Components/App.razor"));
        var combined = boardSrc + (File.Exists(appRazorPath) ? File.ReadAllText(appRazorPath) : "");

        Assert.Contains("board.js", combined);
    }
}
