using Markdig;

namespace KittyClaw.Web.Markdown;

public static class CommentMarkdownPipeline
{
    public static MarkdownPipeline Build()
        => Configure(new MarkdownPipelineBuilder()).Build();

    public static MarkdownPipelineBuilder Configure(MarkdownPipelineBuilder builder)
        => builder
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak();
}
