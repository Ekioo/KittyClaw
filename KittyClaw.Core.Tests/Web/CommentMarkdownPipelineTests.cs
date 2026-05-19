using Markdig;
using KittyClaw.Web.Markdown;

namespace KittyClaw.Core.Tests.Web;

public class CommentMarkdownPipelineTests
{
    private static string Render(string input)
    {
        var pipeline = CommentMarkdownPipeline.Build();
        return Markdig.Markdown.ToHtml(input ?? string.Empty, pipeline);
    }

    [Fact]
    public void SingleNewline_BecomesBr()
    {
        var html = Render("Line A\nLine B");
        Assert.Contains("<br", html);
        Assert.Contains("Line A", html);
        Assert.Contains("Line B", html);
    }

    [Fact]
    public void BlankLine_ProducesTwoParagraphsWithoutBr()
    {
        var html = Render("Para 1\n\nPara 2");
        var first = html.IndexOf("<p>", StringComparison.Ordinal);
        var second = html.IndexOf("<p>", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, $"Expected two <p> blocks, got: {html}");
        var between = html.Substring(first, second - first);
        Assert.DoesNotContain("<br", between);
    }

    [Fact]
    public void SingleLine_HasNoBr()
    {
        var html = Render("Hello world");
        Assert.DoesNotContain("<br", html);
        Assert.Contains("Hello world", html);
    }

    [Fact]
    public void CrlfNewline_BecomesBr()
    {
        var html = Render("Line A\r\nLine B");
        Assert.Contains("<br", html);
    }

    [Fact]
    public void EmptyInput_DoesNotThrow()
    {
        var html = Render("");
        Assert.NotNull(html);
        Assert.DoesNotContain("<br", html);
    }

    [Fact]
    public void WhitespaceOnlyInput_DoesNotThrow()
    {
        var html = Render("   ");
        Assert.NotNull(html);
        Assert.DoesNotContain("<br", html);
    }

    [Fact]
    public void TrailingNewline_DoesNotProduceDanglingBr()
    {
        var html = Render("Hi\n");
        Assert.DoesNotContain("<br", html);
        Assert.Contains("Hi", html);
    }
}
