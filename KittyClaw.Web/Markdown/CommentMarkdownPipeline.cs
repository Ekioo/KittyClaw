using Markdig;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Markdown;

public static class CommentMarkdownPipeline
{
    public static MarkdownPipeline Build()
        => Configure(new MarkdownPipelineBuilder()).Build();

    public static MarkdownPipelineBuilder Configure(MarkdownPipelineBuilder builder)
        => SafeMarkdown.Configure(builder).UseSoftlineBreakAsHardlineBreak();
}
