using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests;

public class ReadmeDashboardSectionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string ReadmeContent =>
        File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

    [Fact]
    public void Readme_HasDashboardHeading()
    {
        Assert.Contains("## Dashboard", ReadmeContent);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("table")]
    [InlineData("kpi")]
    [InlineData("kpi-grid")]
    [InlineData("progress")]
    [InlineData("sparkline")]
    [InlineData("bar-chart")]
    [InlineData("donut")]
    [InlineData("gauge")]
    [InlineData("status-grid")]
    [InlineData("heatmap")]
    [InlineData("leaderboard")]
    [InlineData("timeline")]
    [InlineData("image")]
    [InlineData("mermaid")]
    public void Readme_MentionsTileTemplate(string templateId)
    {
        Assert.Contains(templateId, ReadmeContent);
    }

    [Fact]
    public void Readme_HasScreenshotReference()
    {
        var content = ReadmeContent;
        // Either Markdown image syntax or HTML <img with src ending in dashboard.png
        bool hasMarkdown = Regex.IsMatch(content, @"!\[.*?\]\(.*?dashboard\.png\)");
        bool hasHtml = Regex.IsMatch(content, @"<img[^>]+src=[""'][^""']*dashboard\.png[""']");
        Assert.True(hasMarkdown || hasHtml,
            "README.md must contain an image reference whose src ends with dashboard.png");
    }

    [Fact]
    public void Readme_HasTileYamlReference()
    {
        Assert.Contains("tile.yaml", ReadmeContent);
    }

    [Fact]
    public void ScreenshotFile_ExistsAtCanonicalPath()
    {
        var path = Path.Combine(RepoRoot, "docs", "assets", "dashboard.png");
        Assert.True(File.Exists(path),
            $"Expected screenshot at {path} — programmer must commit it.");
    }
}
