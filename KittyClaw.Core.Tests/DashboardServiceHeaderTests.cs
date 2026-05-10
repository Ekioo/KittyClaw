using KittyClaw.Core.Services;
using Xunit;

namespace KittyClaw.Core.Tests;

public class DashboardServiceHeaderTests
{
    // --- ParseHeader ---

    [Fact]
    public void ParseHeader_ReturnsNull_WhenNoHeader()
    {
        var result = DashboardService.ParseHeader("# Plain markdown\nNo header here.");
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_ReturnsNull_WhenEmpty()
    {
        Assert.Null(DashboardService.ParseHeader(""));
        Assert.Null(DashboardService.ParseHeader("   "));
    }

    [Fact]
    public void ParseHeader_ParsesMinimalHeader()
    {
        var content = """
            ---
            refresh: 3600
            prompt: List top tickets
            ---
            # Body
            """;
        var h = DashboardService.ParseHeader(content);
        Assert.NotNull(h);
        Assert.Equal(3600, h.RefreshSeconds);
        Assert.Equal("List top tickets", h.Prompt);
        Assert.Null(h.Model);
    }

    [Fact]
    public void ParseHeader_ParsesHeaderWithModel()
    {
        var content = """
            ---
            refresh: 600
            prompt: Summarise backlog
            model: claude-haiku-4-5-20251001
            ---
            """;
        var h = DashboardService.ParseHeader(content);
        Assert.NotNull(h);
        Assert.Equal(600, h.RefreshSeconds);
        Assert.Equal("Summarise backlog", h.Prompt);
        Assert.Equal("claude-haiku-4-5-20251001", h.Model);
    }

    [Fact]
    public void ParseHeader_ReturnsNull_WhenRefreshMissing()
    {
        var content = "---\nprompt: Do something\n---\n";
        Assert.Null(DashboardService.ParseHeader(content));
    }

    [Fact]
    public void ParseHeader_ReturnsNull_WhenPromptMissing()
    {
        var content = "---\nrefresh: 300\n---\n";
        Assert.Null(DashboardService.ParseHeader(content));
    }

    [Fact]
    public void ParseHeader_ReturnsNull_WhenRefreshIsZero()
    {
        var content = "---\nrefresh: 0\nprompt: x\n---\n";
        Assert.Null(DashboardService.ParseHeader(content));
    }

    [Fact]
    public void ParseHeader_ReturnsNull_WhenClosingDelimiterMissing()
    {
        var content = "---\nrefresh: 3600\nprompt: x\n";
        Assert.Null(DashboardService.ParseHeader(content));
    }

    // --- ExtractBody ---

    [Fact]
    public void ExtractBody_ReturnsFullContent_WhenNoHeader()
    {
        var content = "# Plain markdown";
        Assert.Equal("# Plain markdown", DashboardService.ExtractBody(content));
    }

    [Fact]
    public void ExtractBody_StripsHeader()
    {
        var content = "---\nrefresh: 3600\nprompt: x\n---\n# Body\nline2";
        Assert.Equal("# Body\nline2", DashboardService.ExtractBody(content));
    }

    [Fact]
    public void ExtractBody_ReturnsEmpty_WhenOnlyHeader()
    {
        var content = "---\nrefresh: 3600\nprompt: x\n---\n";
        Assert.Equal("", DashboardService.ExtractBody(content));
    }

    // --- BuildContent ---

    [Fact]
    public void BuildContent_ProducesRoundTrippableContent()
    {
        var header = new DashboardFileHeader(1800, "Analyse sprint", "claude-sonnet-4-6");
        var body = "# Sprint summary\nContent here.";
        var built = DashboardService.BuildContent(header, body);

        var parsed = DashboardService.ParseHeader(built);
        Assert.NotNull(parsed);
        Assert.Equal(1800, parsed.RefreshSeconds);
        Assert.Equal("Analyse sprint", parsed.Prompt);
        Assert.Equal("claude-sonnet-4-6", parsed.Model);

        var extractedBody = DashboardService.ExtractBody(built);
        Assert.Equal(body, extractedBody);
    }

    [Fact]
    public void BuildContent_OmitsModelLine_WhenNull()
    {
        var header = new DashboardFileHeader(300, "Quick check", null);
        var built = DashboardService.BuildContent(header, "body");
        Assert.DoesNotContain("model:", built);
    }
}
