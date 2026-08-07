namespace KittyClaw.Core.Tests.Web;

public sealed class FocusStyleContractTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void PipelineTabs_OverridePrimaryHoverAndUseSharedFocusRing()
    {
        var css = KittyClaw.Core.Tests.Helpers.AppCssHelper.ReadAll(RepoRoot());
        var hoverRule = Rule(css, ".pipeline-tab:hover");
        var focusRule = Rule(css, ".pipeline-tab:focus-visible");

        Assert.Contains("--focus-ring: #60a5fa", css);
        Assert.Contains("background: var(--surface3)", hoverRule);
        Assert.Contains("color: var(--text)", hoverRule);
        Assert.Contains("outline-color: var(--focus-ring)", focusRule);
    }

    [Fact]
    public void GraphicCharter_DistinguishesHoverSelectionAndKeyboardFocus()
    {
        var charter = File.ReadAllText(Path.Combine(RepoRoot(), "doc", "graphic-charter.md"));

        Assert.Contains("## Interaction states and focus", charter);
        Assert.Contains("Hover, selection, and keyboard focus", charter);
        Assert.Contains("2px solid var(--focus-ring)", charter);
        Assert.Contains("must not replace the component's background or text color", charter);
    }

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing CSS selector {selector}.");
        var end = css.IndexOf('}', start);
        Assert.True(end > start, $"Unclosed CSS rule {selector}.");
        return css[start..(end + 1)];
    }
}
