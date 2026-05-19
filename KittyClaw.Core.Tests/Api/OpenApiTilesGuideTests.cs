using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Tests for the "Dashboard tiles" guide section of the generated /api/docs markdown.
/// RED until OpenApiMarkdownGenerator emits the guide and TileSidecar fields carry
/// XML doc comments describing the schema.
/// </summary>
public sealed class OpenApiTilesGuideTests : IClassFixture<OpenApiDocumentationTests.ApiFactory>, IDisposable
{
    private static readonly string[] AllTemplates =
    {
        "markdown", "table", "kpi", "kpi-grid", "progress", "sparkline",
        "bar-chart", "donut", "gauge", "status-grid", "heatmap",
        "leaderboard", "timeline", "image", "mermaid",
    };

    private readonly HttpClient _client;

    public OpenApiTilesGuideTests(OpenApiDocumentationTests.ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    private async Task<string> GetMarkdownAsync()
    {
        var json = await _client.GetStringAsync("/openapi/v1.json");
        using var doc = JsonDocument.Parse(json);
        return OpenApiMarkdownGenerator.Generate(doc);
    }

    // Case 1: Guide section heading present.
    [Fact]
    public async Task Guide_DashboardTiles_HeadingPresent()
    {
        var md = await GetMarkdownAsync();
        Assert.Contains("## Guide: Dashboard tiles", md);
    }

    // Case 2: All 15 templates enumerated.
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
    public async Task Guide_DashboardTiles_ListsTemplate(string template)
    {
        var md = await GetMarkdownAsync();
        var guideStart = md.IndexOf("## Guide: Dashboard tiles", StringComparison.Ordinal);
        Assert.True(guideStart >= 0, "Guide section must exist before checking template list");
        var guideBody = md.Substring(guideStart);
        Assert.Contains(template, guideBody);
    }

    // Case 3: Sidecar YAML format with all 5 keys.
    [Fact]
    public async Task Guide_DashboardTiles_DocumentsSidecarYamlFormat()
    {
        var md = await GetMarkdownAsync();
        var guideStart = md.IndexOf("## Guide: Dashboard tiles", StringComparison.Ordinal);
        Assert.True(guideStart >= 0);
        var guideBody = md.Substring(guideStart);

        Assert.Contains("```yaml", guideBody);
        Assert.Contains("template:", guideBody);
        Assert.Contains("refresh:", guideBody);
        Assert.Contains("prompt:", guideBody);
        Assert.Contains("model:", guideBody);
        Assert.Contains("title:", guideBody);
    }

    // Case 4: Creation walkthrough references the four endpoints in order.
    [Fact]
    public async Task Guide_DashboardTiles_WalkthroughReferencesEndpointsInOrder()
    {
        var md = await GetMarkdownAsync();
        var guideStart = md.IndexOf("## Guide: Dashboard tiles", StringComparison.Ordinal);
        Assert.True(guideStart >= 0);
        var guideBody = md.Substring(guideStart);

        var iRegister = guideBody.IndexOf("POST /dashboard/tiles", StringComparison.Ordinal);
        var iSidecar  = guideBody.IndexOf("/sidecar", StringComparison.Ordinal);
        var iOutput   = guideBody.IndexOf("/output", StringComparison.Ordinal);
        var iRefresh  = guideBody.IndexOf("/refresh", StringComparison.Ordinal);

        Assert.True(iRegister >= 0, "walkthrough must reference POST /dashboard/tiles");
        Assert.True(iSidecar  >  iRegister, "walkthrough must reference /sidecar after register step");
        Assert.True(iOutput   >  iSidecar,  "walkthrough must reference /output after sidecar step");
        Assert.True(iRefresh  >  iOutput,   "walkthrough must reference /refresh last");
    }

    // Case 5: TileSidecar schema enriched — `template` field row mentions an allowed value.
    [Fact]
    public async Task TileSidecar_SchemaTable_TemplateRow_DescribesAllowedValues()
    {
        var md = await GetMarkdownAsync();
        var schemaIdx = md.IndexOf("### TileSidecar", StringComparison.Ordinal);
        Assert.True(schemaIdx >= 0, "TileSidecar schema section must be present");

        // Take a window after the schema header to find the template row.
        var window = md.Substring(schemaIdx, Math.Min(2000, md.Length - schemaIdx));
        var templateRowMatch = Regex.Match(window, @"\|\s*template\s*\|[^\n]*", RegexOptions.IgnoreCase);
        Assert.True(templateRowMatch.Success, "TileSidecar table must contain a 'template' row");
        var row = templateRowMatch.Value;

        // Description column must mention at least one known template name (proves it's not empty).
        Assert.Contains("markdown", row);
    }

    // Case 6: Heading appears exactly once.
    [Fact]
    public async Task Guide_DashboardTiles_HeadingAppearsOnce()
    {
        var md = await GetMarkdownAsync();
        var count = Regex.Matches(md, @"^##\s+Guide:\s+Dashboard tiles\s*$", RegexOptions.Multiline).Count;
        Assert.Equal(1, count);
    }
}
