namespace KittyClaw.Core.Tests.Web;

public sealed class McpGlobalSettingsUiTests
{
    [Fact]
    public void Global_settings_page_exposes_the_persisted_one_click_toggle()
    {
        var source = File.ReadAllText(WebPath("Components", "Pages", "GlobalSettings.razor"));

        Assert.Contains("@page \"/settings\"", source);
        Assert.Contains("data-testid=\"mcp-enabled\"", source);
        Assert.Contains("AppSettings.McpEnabled = e.Value is true", source);
        Assert.Contains("data-testid=\"mcp-status\"", source);
    }

    [Fact]
    public void Mcp_runtime_is_gated_by_global_settings_not_an_environment_variable()
    {
        var source = File.ReadAllText(WebPath("Program.cs"));

        Assert.DoesNotContain("KITTYCLAW_MCP_ENABLED", source);
        Assert.Contains("!appSettings.McpEnabled", source);
        Assert.Contains("app.MapMcp(\"/mcp\")", source);
    }

    private static string WebPath(params string[] parts) =>
        Path.Combine([RepoRoot(), "KittyClaw.Web", .. parts]);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
