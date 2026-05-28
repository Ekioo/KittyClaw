using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Contract tests for ticket #221: ESC closes the fullscreen description/comment
/// editor only, not the ticket panel, with dirty-check confirmation.
/// All assertions are source-text checks — RED on dev, GREEN after the fix.
/// </summary>
public class BoardFullscreenEscTests
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

    private static string BoardRazorPath() =>
        Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor");

    private static string BoardEnJsonPath() =>
        Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "Board.en.json");

    private static string BoardFrJsonPath() =>
        Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "Board.fr.json");

    private static string LoadBoard() => File.ReadAllText(BoardRazorPath());

    // Case 1 / Case 3: _escFullscreenEditor field must be declared.
    // Absence means no registration — ESC falls through to the ticket panel.
    [Fact]
    public void Board_HasEscFullscreenEditorField()
    {
        var src = LoadBoard();
        Assert.Contains("_escFullscreenEditor", src);
    }

    // Case 1 / Case 2: _fullscreenOriginalText field must be declared for dirty tracking.
    [Fact]
    public void Board_HasFullscreenOriginalTextField()
    {
        var src = LoadBoard();
        Assert.Contains("_fullscreenOriginalText", src);
    }

    // Case 1 / Case 2 / Case 3: OpenFullscreen must push onto EscapeKeyStack.
    // The push must reference _escFullscreenEditor (assign to it).
    [Fact]
    public void Board_OpenFullscreen_PushesEscapeHandler()
    {
        var src = LoadBoard();
        // _escFullscreenEditor must appear more than once: declaration + assignment in OpenFullscreen
        var count = Regex.Matches(src, @"_escFullscreenEditor").Count;
        Assert.True(count >= 2, $"Expected _escFullscreenEditor to appear at least twice (declare + assign), found {count}.");
    }

    // Case 1 / Case 2: OpenFullscreen must store _fullscreenOriginalText so dirty checks work.
    [Fact]
    public void Board_OpenFullscreen_StoresOriginalText()
    {
        var src = LoadBoard();
        // _fullscreenOriginalText must be assigned (= operator), not just declared
        Assert.Contains("_fullscreenOriginalText =", src);
    }

    // Case 2: ESC on a dirty editor must invoke JS confirm with the DiscardChangesConfirm key.
    [Fact]
    public void Board_EscHandler_InvokesConfirmForDirtyText()
    {
        var src = LoadBoard();
        Assert.Contains("DiscardChangesConfirm", src);
        // The dirty check must compare _fullscreenText with _fullscreenOriginalText
        Assert.Contains("_fullscreenOriginalText", src);
    }

    // Case 1 / Case 3 / Case 4: CancelFullscreen must dispose _escFullscreenEditor.
    [Fact]
    public void Board_CancelFullscreen_DisposesEscHandler()
    {
        var src = LoadBoard();
        // Both Dispose() and null-clear must appear after _escFullscreenEditor
        Assert.Matches(new Regex(@"_escFullscreenEditor\?\.Dispose\(\)"), src);
    }

    // Case 4 / Case 5: SaveFullscreen must also dispose _escFullscreenEditor.
    // If it does not, the handler leaks and ESC after save would re-close the editor (no-op) instead of the panel.
    [Fact]
    public void Board_SaveFullscreen_DisposesEscHandler()
    {
        var src = LoadBoard();
        // Dispose must appear at least twice: one in CancelFullscreen, one in SaveFullscreen
        var count = Regex.Matches(src, @"_escFullscreenEditor\?\.Dispose\(\)").Count;
        Assert.True(count >= 2, $"Expected _escFullscreenEditor?.Dispose() to appear at least twice (Cancel + Save), found {count}.");
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
