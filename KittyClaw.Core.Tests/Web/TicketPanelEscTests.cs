using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// ESC-closes-the-panel contract (ticket #219), retargeted to the extracted
/// TicketPanel component: the panel owns its escape handling, so it works
/// identically for click-opened, URL-deep-linked and unified-home-opened tickets.
/// All assertions are source-text checks.
/// </summary>
public class TicketPanelEscTests
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

    private static string LoadPanel() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "TicketPanel.razor"));

    private static string LoadBoard() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));

    // The panel registers its close handler once, on first render. Because the component
    // only exists while a ticket is open, this covers every open path (click, deep link,
    // unified home) without per-caller wiring — the #219 bug class can't reappear.
    [Fact]
    public void TicketPanel_PushesEscCloseHandler_OnFirstRender()
    {
        var src = LoadPanel();
        Assert.Contains("if (firstRender)", src);
        Assert.Matches(new Regex(@"_escClose\s*=\s*EscapeStack\.PushWithFocus"), src);
    }

    // The handler must be released when the panel unmounts, whatever the close path.
    [Fact]
    public void TicketPanel_DisposesEscCloseHandler_OnDispose()
    {
        var src = LoadPanel();
        Assert.Matches(new Regex(@"_escClose\s*\?\s*\.\s*Dispose\(\)"), src);
    }

    // The board page must no longer carry its own ticket-panel ESC wiring —
    // a leftover would double-register and make one ESC close two layers.
    [Fact]
    public void Board_NoLongerOwnsTicketPanelEscWiring()
    {
        var src = LoadBoard();
        Assert.DoesNotContain("_escTicketPanel", src);
    }
}
