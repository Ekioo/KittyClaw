using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class TileRendererHeatmapTests
{
    private static string Render(string json) =>
        TileRenderer.Render(TileTemplate.Heatmap, json, "data.json");

    [Fact]
    public void SimpleArray_BackwardCompatible_RendersGrid()
    {
        var json = """[{"date":"2026-04-01","value":5},{"date":"2026-04-02","value":10}]""";
        var html = Render(json);
        Assert.Contains("tile-heatmap-grid", html);
        Assert.Contains("tile-heatmap-l", html);
        Assert.DoesNotContain("tile-heatmap-legend", html);
    }

    [Fact]
    public void ObjectFormat_WithData_RendersGrid()
    {
        var json = """{"data":[{"date":"2026-04-01","value":5}]}""";
        var html = Render(json);
        Assert.Contains("tile-heatmap-grid", html);
        Assert.DoesNotContain("tile-heatmap-legend", html);
    }

    [Fact]
    public void ObjectFormat_WithLegend_RendersLegend()
    {
        var json = """
        {
          "data":[{"date":"2026-04-01","value":5,"color":"#4f9cf7"}],
          "legend":[{"label":"Commits","color":"#4f9cf7"}]
        }
        """;
        var html = Render(json);
        Assert.Contains("tile-heatmap-legend", html);
        Assert.Contains("tile-heatmap-legend-swatch", html);
        Assert.Contains("Commits", html);
        Assert.Contains("#4f9cf7", html);
    }

    [Fact]
    public void ColoredEntries_EmitRgbaInlineStyle()
    {
        var json = """[{"date":"2026-04-01","value":8,"color":"#ff8800"}]""";
        var html = Render(json);
        Assert.Contains("rgba(255,136,0,", html);
        Assert.DoesNotContain("tile-heatmap-l4", html);
    }

    [Fact]
    public void IntensityScaledPerColorGroup()
    {
        // Blue group max=10, orange group max=5 — each group's l4 cell should be fully opaque.
        var json = """
        [
          {"date":"2026-04-07","value":10,"color":"#0000ff"},
          {"date":"2026-04-08","value":5,"color":"#ff8000"}
        ]
        """;
        var html = Render(json);
        // Both should be level 4 (1.00 alpha) because each is the max of its group.
        Assert.Equal(2, CountOccurrences(html, "rgba("));
        Assert.Equal(2, CountOccurrences(html, ",1.00)"));
    }

    [Fact]
    public void ShortHexColor_Parsed()
    {
        var json = """[{"date":"2026-04-01","value":5,"color":"#f80"}]""";
        var html = Render(json);
        // #f80 → #ff8800 → rgb(255,136,0)
        Assert.Contains("rgba(255,136,0,", html);
    }

    [Fact]
    public void InvalidHexColor_FallsBackToAccentLevels()
    {
        var json = """[{"date":"2026-04-01","value":5,"color":"not-a-color"}]""";
        var html = Render(json);
        Assert.Contains("tile-heatmap-l", html);
        Assert.DoesNotContain("rgba(", html);
    }

    [Fact]
    public void EmptyData_ReturnsEmptyPlaceholder()
    {
        var html = Render("[]");
        Assert.Contains("tile-empty", html);
    }

    private static int CountOccurrences(string text, string sub)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(sub, index, StringComparison.Ordinal)) >= 0) { count++; index += sub.Length; }
        return count;
    }
}
