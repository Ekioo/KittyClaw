namespace KittyClaw.Core.Tests.Web;

public sealed class DashboardGuidanceWebTests
{
    [Fact]
    public void Dashboard_exposes_stable_progress_activity_and_action_selectors()
    {
        var source = File.ReadAllText(WebPath("Components", "Pages", "Dashboard.razor"));

        Assert.Contains("data-testid=\"dashboard-guidance\"", source);
        Assert.Contains("data-testid=\"dashboard-primary-action\"", source);
        Assert.Contains("data-testid=\"dashboard-real-activity\"", source);
        Assert.Contains("settings_opened_before_first_result", source);
        Assert.Contains("guidance_replaced_by_activity", source);
    }

    private static string WebPath(params string[] parts)
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "KittyClaw.Web")))
            root = Directory.GetParent(root)?.FullName;
        return Path.Combine(new[] { root!, "KittyClaw.Web" }.Concat(parts).ToArray());
    }
}
