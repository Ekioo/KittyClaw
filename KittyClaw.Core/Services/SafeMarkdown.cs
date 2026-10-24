using Markdig;
using Markdig.Extensions.GenericAttributes;

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
        return builder;
    }

    public static MarkdownPipeline Build(bool softLineBreaks = false)
    {
        var builder = Configure(new MarkdownPipelineBuilder());
        if (softLineBreaks) builder.UseSoftlineBreakAsHardlineBreak();
        return builder.Build();
    }
}
