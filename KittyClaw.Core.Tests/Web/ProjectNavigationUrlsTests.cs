using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public class ProjectNavigationUrlsTests
{
    [Theory]
    [InlineData(null, "/board/demo?pipeline=42")]
    [InlineData("approvals", "/board/demo/approvals?pipeline=42")]
    [InlineData("workflows", "/board/demo/workflows?pipeline=42")]
    [InlineData("settings", "/board/demo/settings?pipeline=42")]
    public void Project_navigation_preserves_the_originating_pipeline(string? page, string expected)
        => Assert.Equal(expected, ProjectNavigationUrls.Page("demo", 42, page));

    [Theory]
    [InlineData(null, "/board/demo")]
    [InlineData("settings", "/board/demo/settings")]
    public void Direct_navigation_without_a_pipeline_keeps_the_default_route(string? page, string expected)
        => Assert.Equal(expected, ProjectNavigationUrls.Page("demo", null, page));

    [Theory]
    [InlineData(42, "dashboard", "/board/demo/dashboard?pipeline=42")]
    [InlineData(null, "dashboard", "/board/demo/dashboard")]
    [InlineData(42, null, "/board/demo?pipeline=42")]
    [InlineData(42, "https://example.com", "/board/demo?pipeline=42")]
    public void Return_restores_only_supported_project_views(int? pipeline, string? view, string expected)
        => Assert.Equal(expected, ProjectNavigationUrls.Return("demo", pipeline, view));

    [Theory]
    [InlineData(42, "/board/demo/settings?pipeline=42&view=dashboard")]
    [InlineData(null, "/board/demo/settings?view=dashboard")]
    public void Dashboard_origin_survives_project_settings_cross_links(int? pipeline, string expected)
        => Assert.Equal(expected, ProjectNavigationUrls.Page("demo", pipeline, "settings", "dashboard"));
}
