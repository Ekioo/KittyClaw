using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Verifies that Board.razor wires _escTicketPanel through EscapeKeyStack
/// for URL-loaded ticket panels (ticket #219).
/// All assertions are source-text checks — RED on dev, GREEN after the fix.
/// </summary>
public class BoardEscTicketPanelTests
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

    private static string LoadBoardRazor() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));

    // Case 1 + Case 5: OnAfterRenderAsync must push the ESC handler when
    // _selectedTicket is non-null and _escTicketPanel is still null.
    // The guard prevents double-registration on subsequent re-renders.
    [Fact]
    public void Board_OnAfterRenderAsync_PushesEscTicketPanel_WhenSelectedAndNotYetPushed()
    {
        var src = LoadBoardRazor();
        // Must contain the idempotent guard that pushes on first render after open
        Assert.Contains("_selectedTicket is not null && _escTicketPanel is null", src);
    }

    // Case 1: The push call itself must appear in OnAfterRenderAsync context —
    // verify that EscapeStack.PushWithFocus is called with ClosePanel inside the guard.
    [Fact]
    public void Board_OnAfterRenderAsync_CallsPushWithFocus_ForTicketPanel()
    {
        var src = LoadBoardRazor();
        // The new push block must reference ClosePanel (as the callback) and assign _escTicketPanel
        // We assert both sides of the assignment exist in the file beyond the SelectTicket call.
        // Count occurrences of the assignment: SelectTicket already has one, the new guard adds a second.
        var matches = Regex.Matches(src, @"_escTicketPanel\s*=\s*EscapeStack\.PushWithFocus");
        Assert.True(matches.Count >= 2,
            $"Expected at least 2 assignments of _escTicketPanel via EscapeStack.PushWithFocus " +
            $"(one in SelectTicket, one in OnAfterRenderAsync), found {matches.Count}.");
    }

    // Case 5: OnParametersSetAsync must dispose + null the stale ESC token
    // when TicketId changes to a different ticket, so a fresh push is triggered.
    [Fact]
    public void Board_OnParametersSetAsync_DisposesEscTicketPanel_OnTicketIdChange()
    {
        var src = LoadBoardRazor();
        // The dispose+null must appear in the block that handles TicketId changes.
        // We look for the pattern near the TicketId comparison block.
        // A strict literal substring is safe — no such pattern currently exists in dev.
        Assert.Contains("_escTicketPanel?.Dispose()", src.Replace("\r\n", "\n").Replace("\r", "\n"));
        // Additionally, _escTicketPanel must be nulled after dispose so OnAfterRenderAsync re-pushes.
        // Verify the null assignment appears more than once (ClosePanel already has one).
        var nullAssignments = Regex.Matches(src, @"_escTicketPanel\s*=\s*null\b");
        Assert.True(nullAssignments.Count >= 2,
            $"Expected _escTicketPanel = null in both ClosePanel and OnParametersSetAsync, found {nullAssignments.Count}.");
    }

    // Case 2 (regression): SelectTicket must still push _escTicketPanel — existing wiring must not be removed.
    [Fact]
    public void Board_SelectTicket_StillPushesEscTicketPanel()
    {
        var src = LoadBoardRazor();
        // SelectTicket is the click path; it must still call PushWithFocus.
        // This was already present in dev, so this test should be GREEN now — kept as regression guard.
        Assert.Matches(new Regex(@"_escTicketPanel\s*=\s*EscapeStack\.PushWithFocus"), src);
    }

    // Case 3 (regression): ClosePanel must still dispose _escTicketPanel.
    [Fact]
    public void Board_ClosePanel_StillDisposesEscTicketPanel()
    {
        var src = LoadBoardRazor();
        // ClosePanel already has _escTicketPanel?.Dispose() — assert it is present.
        Assert.Matches(new Regex(@"_escTicketPanel\s*\?\s*\.\s*Dispose\(\)"), src);
    }

    // Case 6 (edge): No push when TicketId param is absent.
    // Guard `_selectedTicket is not null` ensures this — assert it is present.
    [Fact]
    public void Board_OnAfterRenderAsync_DoesNotPush_WhenNoTicketSelected()
    {
        var src = LoadBoardRazor();
        // The null-check on _selectedTicket is the sole guard that prevents a stale push.
        // This test reaffirms Case 1's guard. Distinct assertion: the condition is not inverted.
        Assert.DoesNotContain("_selectedTicket is null && _escTicketPanel is null", src);
        Assert.Contains("_selectedTicket is not null && _escTicketPanel is null", src);
    }
}
