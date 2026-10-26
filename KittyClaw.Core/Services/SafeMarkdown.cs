using Markdig;
using Markdig.Extensions.GenericAttributes;
using Markdig.Renderers;

namespace KittyClaw.Core.Services;

/// <summary>Shared Markdown configuration for untrusted agent and project content.</summary>
public static class SafeMarkdown
{
    public static MarkdownPipelineBuilder Configure(MarkdownPipelineBuilder builder)
    {
        builder.UseAdvancedExtensions().DisableHtml();
        // DisableHtml only rejects raw HTML. Generic attributes can independently
        // emit event handlers or other active HTML attributes on generated nodes.
        foreach (var extension in builder.Extensions.OfType<GenericAttributesExtension>().ToArray())
            builder.Extensions.Remove(extension);
        if (!builder.Extensions.OfType<SafeLinkExtension>().Any())
            builder.Extensions.Add(new SafeLinkExtension());
        return builder;
    }

    public static MarkdownPipeline Build(bool softLineBreaks = false)
    {
        var builder = Configure(new MarkdownPipelineBuilder());
        if (softLineBreaks) builder.UseSoftlineBreakAsHardlineBreak();
        return builder.Build();
    }

    private sealed class SafeLinkExtension : IMarkdownExtension
    {
        public void Setup(MarkdownPipelineBuilder pipeline) { }

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is not HtmlRenderer html) return;
            var previous = html.LinkRewriter;
            html.LinkRewriter = url => SafeUrl(previous is null ? url : previous(url));
        }
    }

    private static string SafeUrl(string url)
    {
        // DisableHtml does not prevent Markdown links from emitting executable URI schemes.
        // Reject browser-ignored controls before classifying the decoded scheme.
        var decoded = System.Net.WebUtility.HtmlDecode(url).Trim();
        if (decoded.Any(char.IsControl)) return "#";
        var colon = decoded.IndexOf(':');
        var relativeBoundary = decoded.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || relativeBoundary >= 0 && relativeBoundary < colon) return url;
        return Uri.TryCreate(decoded, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https" or "mailto" or "tel" ? url : "#";
    }
}
