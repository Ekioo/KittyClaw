using System.Net;
using Markdig;

namespace KittyClaw.Web.Markdown;

public static class ChatMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static string Render(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        try
        {
            return Markdig.Markdown.ToHtml(text, Pipeline);
        }
        catch
        {
            var encoded = WebUtility.HtmlEncode(text);
            return "<p class=\"chat-md-fallback\">" + encoded + "</p>" +
                   "<p class=\"chat-md-fallback-note\">[message could not be formatted as Markdown — content shown as plain text. Try simplifying nested lists/quotes.]</p>";
        }
    }
}
