using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Regression tests for ticket #221 owner feedback, retargeted to TicketPanel:
/// (1) No browser window.confirm — use the integrated confirm modal.
/// (2) After canceling the confirm modal, pressing ESC must re-trigger the dirty-check
///     (the handler must re-register itself on the EscapeKeyStack after a cancel).
/// All assertions are source-text checks.
/// </summary>
public class TicketPanelFullscreenEscRegressionTests
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

    // Owner feedback: must NOT call the browser window.confirm().
    [Fact]
    public void TicketPanel_EscHandler_DoesNotUseBrowserConfirm()
    {
        var src = LoadPanel();
        Assert.DoesNotContain("InvokeAsync<bool>(\"confirm\"", src);
    }

    // Integrated modal: a bool field to control its visibility must be declared.
    [Fact]
    public void TicketPanel_HasShowDiscardConfirmField()
    {
        var src = LoadPanel();
        Assert.Contains("_showDiscardConfirm", src);
    }

    // ESC handler (dirty path): must set _showDiscardConfirm = true to show the modal.
    [Fact]
    public void TicketPanel_EscHandler_SetsShowDiscardConfirmTrue()
    {
        var src = LoadPanel();
        Assert.Contains("_showDiscardConfirm = true", src);
    }

    // Integrated modal must appear in the Razor markup (rendered conditionally on the field).
    // We require _showDiscardConfirm to appear at least 3 times:
    //   declaration, "= true" (set from ESC handler), and at least one markup reference.
    [Fact]
    public void TicketPanel_DiscardConfirmModal_RenderedConditionally()
    {
        var src = LoadPanel();
        var count = Regex.Matches(src, @"_showDiscardConfirm").Count;
        Assert.True(count >= 3,
            $"Expected _showDiscardConfirm to appear at least 3 times (declare + set + markup), found {count}.");
    }

    // After the user cancels the discard-confirm modal, the ESC handler must be re-registered
    // so that subsequent ESC presses still trigger the dirty-check. Both OpenFullscreen and
    // CancelDiscard go through the shared ArmFullscreenEscape registration.
    [Fact]
    public void TicketPanel_EscHandlerReregisteredAfterCancelDiscard()
    {
        var src = LoadPanel();
        var count = Regex.Matches(src, @"ArmFullscreenEscape\(\);").Count;
        Assert.True(count >= 2,
            $"Expected ArmFullscreenEscape() to be called at least twice " +
            $"(initial registration in OpenFullscreen + re-registration in CancelDiscard), found {count}.");
    }
}
