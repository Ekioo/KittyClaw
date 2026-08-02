using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class DashboardModelRoutingTests
{
    [Fact]
    public void Resolve_includes_the_project_quota_fallback()
    {
        var project = new Project
        {
            Name = "Dashboard project",
            Slug = "dashboard-project",
            FallbackModel = "claude:claude-sonnet-4-6",
        };

        var (primary, fallback) = DashboardModelRouting.Resolve(project, "claude-haiku-4-5");

        Assert.Equal(CliProvider.Claude, primary.Provider);
        Assert.Equal("claude-haiku-4-5", primary.Model);
        Assert.NotNull(fallback);
        Assert.Equal(CliProvider.Claude, fallback.Provider);
        Assert.Equal("claude-sonnet-4-6", fallback.Model);
    }

    [Fact]
    public void Resolve_ignores_an_unusable_fallback_without_breaking_the_primary()
    {
        var project = new Project
        {
            Name = "Dashboard project",
            Slug = "dashboard-project",
            FallbackModel = "local-model-without-a-base-url",
        };

        var (primary, fallback) = DashboardModelRouting.Resolve(project, "claude-haiku-4-5");

        Assert.Null(primary.ValidationError);
        Assert.Null(fallback);
    }

    [Fact]
    public void Both_dashboard_refresh_paths_clear_primary_quota_output_before_fallback_output()
    {
        var root = RepoRoot();
        var service = File.ReadAllText(Path.Combine(
            root, "KittyClaw.Core", "Services", "DashboardRefreshService.cs"));
        var page = File.ReadAllText(Path.Combine(
            root, "KittyClaw.Web", "Components", "Pages", "Dashboard.razor"));

        Assert.Contains("FallbackTarget = fallbackTarget", service);
        Assert.Contains("FallbackTarget = fallbackTarget", page);
        Assert.Contains("if (ev.Kind == \"fallback\")", service);
        Assert.Contains("if (ev.Kind == \"fallback\")", page);
        Assert.Contains("output.Clear()", service);
        Assert.Contains("output.Clear()", page);
    }

    private static string RepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return directory!;
    }
}
