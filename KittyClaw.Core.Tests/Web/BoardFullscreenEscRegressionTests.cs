using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Regression tests for ticket #221 owner feedback:
/// (1) No browser window.confirm — use integrated confirm modal.
/// (2) After canceling the confirm modal, pressing ESC must re-trigger the dirty-check
///     (the handler must re-register itself on the EscapeKeyStack after a cancel).
/// All assertions are source-text checks — RED on the current impl, GREEN after the fix.
/// </summary>
public class BoardFullscreenEscRegressionTests
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

    private static string LoadBoard() => File.ReadAllText(BoardRazorPath());

    // Owner feedback: must NOT call the browser window.confirm().
    // RED: current impl calls JS.InvokeAsync<bool>("confirm", ...).
    [Fact]
    public void Board_EscHandler_DoesNotUseBrowserConfirm()
    {
        var src = LoadBoard();
        Assert.DoesNotContain("InvokeAsync<bool>(\"confirm\"", src);
    }

    // Integrated modal: a bool field to control its visibility must be declared.
    // RED: _showDiscardConfirm does not exist yet.
    [Fact]
    public void Board_HasShowDiscardConfirmField()
    {
        var src = LoadBoard();
        Assert.Contains("_showDiscardConfirm", src);
    }

    // ESC handler (dirty path): must set _showDiscardConfirm = true to show the modal.
    // RED: current impl calls JS confirm, no such assignment.
    [Fact]
    public void Board_EscHandler_SetsShowDiscardConfirmTrue()
    {
        var src = LoadBoard();
        Assert.Contains("_showDiscardConfirm = true", src);
    }

    // Integrated modal must appear in the Razor markup (rendered conditionally on the field).
    // We require _showDiscardConfirm to appear at least 3 times:
    //   declaration, "= true" (set from ESC handler), and at least one markup reference (@if or bind).
    // RED: field does not exist so count is 0.
    [Fact]
    public void Board_DiscardConfirmModal_RenderedConditionally()
    {
        var src = LoadBoard();
        var count = Regex.Matches(src, @"_showDiscardConfirm").Count;
        Assert.True(count >= 3,
            $"Expected _showDiscardConfirm to appear at least 3 times (declare + set + markup), found {count}.");
    }

    // After the user cancels the discard-confirm modal, the ESC handler must be re-registered
    // so that subsequent ESC presses still trigger the dirty-check.
    // This requires a second PushWithFocus call for _escFullscreenEditor (re-registration path).
    // RED: currently only 1 assignment "_escFullscreenEditor = EscapeStack.PushWithFocus".
    [Fact]
    public void Board_EscHandlerReregisteredAfterCancelDiscard()
    {
        var src = LoadBoard();
        var count = Regex.Matches(src, @"_escFullscreenEditor\s*=\s*EscapeStack\.PushWithFocus").Count;
        Assert.True(count >= 2,
            $"Expected _escFullscreenEditor = EscapeStack.PushWithFocus to appear at least twice " +
            $"(initial registration + re-registration after cancel), found {count}.");
    }
}
