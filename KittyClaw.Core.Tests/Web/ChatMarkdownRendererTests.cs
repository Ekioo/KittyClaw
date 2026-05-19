using System.Linq;
using KittyClaw.Web.Markdown;

namespace KittyClaw.Core.Tests.Web;

public class ChatMarkdownRendererTests
{
    [Fact]
    public void Render_PlainText_ReturnsHtmlParagraph()
    {
        var html = ChatMarkdownRenderer.Render("hello");
        Assert.Contains("<p>hello</p>", html);
        Assert.DoesNotContain("chat-md-fallback", html);
    }

    [Fact]
    public void Render_NormalMarkdown_RendersList()
    {
        var html = ChatMarkdownRenderer.Render("- a\n- b");
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>a</li>", html);
        Assert.DoesNotContain("chat-md-fallback", html);
    }

    [Fact]
    public void Render_Null_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ChatMarkdownRenderer.Render(null));
    }

    [Fact]
    public void Render_Empty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ChatMarkdownRenderer.Render(""));
    }

    [Fact]
    public void Render_DeeplyNestedBlockquotes_DoesNotThrow_AndReturnsFallback()
    {
        var input = string.Concat(Enumerable.Repeat("> ", 200)) + "boom";

        string html = null!;
        var ex = Record.Exception(() => html = ChatMarkdownRenderer.Render(input));

        Assert.Null(ex);
        Assert.Contains("boom", html);
        Assert.Contains("chat-md-fallback-note", html);
    }

    [Fact]
    public void Render_DeeplyNestedLists_DoesNotThrow_AndReturnsFallback()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            sb.Append(new string(' ', i * 2));
            sb.Append("- x\n");
        }
        var input = sb.ToString();

        string html = null!;
        var ex = Record.Exception(() => html = ChatMarkdownRenderer.Render(input));

        Assert.Null(ex);
        Assert.Contains("chat-md-fallback-note", html);
    }

    [Fact]
    public void Render_FallbackPreservesAngleBrackets()
    {
        var input = string.Concat(Enumerable.Repeat("> ", 200)) + "<script>alert(1)</script>";

        var html = ChatMarkdownRenderer.Render(input);

        Assert.Contains("chat-md-fallback-note", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }
}
