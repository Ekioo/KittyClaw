using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KittyClaw.Web.Markdown;

public static class TicketCoverImage
{
    private static readonly MarkdownPipeline Pipeline = CommentMarkdownPipeline.Build();

    public static string? Find(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var document = Markdig.Markdown.Parse(description, Pipeline);
        foreach (var image in document.Descendants<LinkInline>().Where(link => link.IsImage))
        {
            var url = image.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;
            // Root-relative URLs are interpreted as file URIs on Unix.
            if (!url.StartsWith("//") && !url.Contains('\\') && !url.Contains(':'))
                return url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            {
                if (absolute.Scheme is "http" or "https") return url;
            }
        }

        return null;
    }
}
