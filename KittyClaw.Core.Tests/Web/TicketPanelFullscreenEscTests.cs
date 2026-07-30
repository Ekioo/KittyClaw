using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Contract tests for ticket #221, retargeted to the extracted TicketPanel component:
/// ESC closes the fullscreen description/comment editor only, not the ticket panel,
/// with dirty-check confirmation. All assertions are source-text checks.
/// </summary>
public class TicketPanelFullscreenEscTests
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

    private static string PanelRazorPath() =>
        Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "TicketPanel.razor");

    private static string BoardEnJsonPath() =>
        Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "Board.en.json");

    private static string BoardFrJsonPath() =>
        Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "Board.fr.json");

    private static string LoadPanel() => File.ReadAllText(PanelRazorPath());

    // _escFullscreenEditor field must be declared.
    // Absence means no registration — ESC would fall through to the ticket panel.
    [Fact]
    public void TicketPanel_HasEscFullscreenEditorField()
    {
        var src = LoadPanel();
        Assert.Contains("_escFullscreenEditor", src);
    }

    // _fullscreenOriginalText field must be declared for dirty tracking.
    [Fact]
    public void TicketPanel_HasFullscreenOriginalTextField()
    {
        var src = LoadPanel();
        Assert.Contains("_fullscreenOriginalText", src);
    }

    // OpenFullscreen must push onto EscapeKeyStack (via ArmFullscreenEscape).
    [Fact]
    public void TicketPanel_OpenFullscreen_PushesEscapeHandler()
    {
        var src = LoadPanel();
        Assert.Matches(new Regex(@"_escFullscreenEditor\s*=\s*EscapeStack\.PushWithFocus"), src);
        // OpenFullscreen must arm the handler.
        Assert.Contains("ArmFullscreenEscape();", src);
    }

    // OpenFullscreen must store _fullscreenOriginalText so dirty checks work.
    [Fact]
    public void TicketPanel_OpenFullscreen_StoresOriginalText()
    {
        var src = LoadPanel();
        Assert.Contains("_fullscreenOriginalText = _fullscreenText", src);
    }

    // ESC on a dirty editor must open the integrated discard-confirm modal.
    [Fact]
    public void TicketPanel_EscHandler_ChecksDirtyText()
    {
        var src = LoadPanel();
        Assert.Contains("DiscardChangesConfirm", src);
        Assert.Contains("_fullscreenText != _fullscreenOriginalText", src);
    }

    // CancelFullscreen must dispose _escFullscreenEditor.
    [Fact]
    public void TicketPanel_CancelFullscreen_DisposesEscHandler()
    {
        var src = LoadPanel();
        Assert.Matches(new Regex(@"_escFullscreenEditor\?\.Dispose\(\)"), src);
    }

    // SaveFullscreen must also dispose _escFullscreenEditor — otherwise the handler
    // leaks and ESC after save would re-close the editor (no-op) instead of the panel.
    [Fact]
    public void TicketPanel_SaveFullscreen_DisposesEscHandler()
    {
        var src = LoadPanel();
        var count = Regex.Matches(src, @"_escFullscreenEditor\?\.Dispose\(\)").Count;
        Assert.True(count >= 3, $"Expected _escFullscreenEditor?.Dispose() in ArmFullscreenEscape/Cancel, Save and Dispose, found {count}.");
    }

    // Edge: DiscardChangesConfirm key must exist in Board.en.json localization.
    [Fact]
    public void BoardEnJson_HasDiscardChangesConfirmKey()
    {
        var json = File.ReadAllText(BoardEnJsonPath());
        Assert.Contains("DiscardChangesConfirm", json);
    }

    // Edge: DiscardChangesConfirm key must exist in Board.fr.json localization.
    [Fact]
    public void BoardFrJson_HasDiscardChangesConfirmKey()
    {
        var json = File.ReadAllText(BoardFrJsonPath());
        Assert.Contains("DiscardChangesConfirm", json);
    }
}
