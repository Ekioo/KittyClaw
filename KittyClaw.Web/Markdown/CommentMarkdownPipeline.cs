using Markdig;

namespace KittyClaw.Web.Markdown;

public static class CommentMarkdownPipeline
{
    public static MarkdownPipeline Build()
        => Configure(new MarkdownPipelineBuilder()).Build();

    public static MarkdownPipelineBuilder Configure(MarkdownPipelineBuilder builder)
        => builder
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            // Content comes from agents and the unauthenticated REST API; raw HTML would be
            // rendered via MarkupString and execute in the Blazor circuit.
            .DisableHtml();
}
