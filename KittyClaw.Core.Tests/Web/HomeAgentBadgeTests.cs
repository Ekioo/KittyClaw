using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

public class HomeAgentBadgeTests
{
    private static string LoadHomeRazor()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "KittyClaw.Web", "Components", "Pages", "Home.razor");
        Assert.True(File.Exists(path), $"Home.razor not found at {path}");
        return File.ReadAllText(path);
    }

    private static string LoadAppCss()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "KittyClaw.Web", "wwwroot", "app.css");
        Assert.True(File.Exists(path), $"app.css not found at {path}");
        return File.ReadAllText(path);
    }

    // Case 1 & 2: AgentRunsState is injected — prerequisite for badge appearing/disappearing
    [Fact]
    public void Home_InjectsAgentRunsState()
    {
        var src = LoadHomeRazor();
        Assert.Matches(new Regex(@"@inject\s+.*AgentRunsState"), src);
    }

    // Case 1 & 2: Component subscribes to OnChange so badge reacts to run start/end events
    [Fact]
    public void Home_SubscribesToOnChange()
    {
        var src = LoadHomeRazor();
        Assert.Contains("OnChange", src);
        // Must subscribe (+=) not merely reference
        Assert.Matches(new Regex(@"OnChange\s*\+="), src);
    }

    // Case 1 & 2: Component unsubscribes in Dispose — prevents memory leaks and double-fire
    [Fact]
    public void Home_UnsubscribesOnChangeInDispose()
    {
        var src = LoadHomeRazor();
        Assert.Matches(new Regex(@"OnChange\s*-="), src);
    }

    // Case 1: Badge renders conditionally on ActiveForProject(...).Any()
    [Fact]
    public void Home_RendersAgentBadge_WhenActiveForProjectAny()
    {
        var src = LoadHomeRazor();
        Assert.Matches(new Regex(@"ActiveForProject\(.*\)\.Any\(\)"), src);
    }

    // Case 1 & 3: Badge element carries the project-card-agent-badge CSS class
    [Fact]
    public void Home_AgentBadge_HasCorrectCssClass()
    {
        var src = LoadHomeRazor();
        Assert.Contains("project-card-agent-badge", src);
    }

    // Case 3: Badge is rendered inside the per-project loop (project-card-wrap context)
    [Fact]
    public void Home_AgentBadge_IsInsideProjectCardWrap()
    {
        var src = LoadHomeRazor();
        var wrapIndex = src.IndexOf("project-card-wrap", StringComparison.Ordinal);
        var badgeIndex = src.IndexOf("project-card-agent-badge", StringComparison.Ordinal);
        Assert.True(wrapIndex >= 0, "project-card-wrap not found");
        Assert.True(badgeIndex > wrapIndex, "project-card-agent-badge must appear after project-card-wrap in the template");
    }

    // Case 5: project-paused class and badge can coexist — both reference the same project-card-wrap div
    [Fact]
    public void Home_PausedClass_AndAgentBadge_BothInCardWrap()
    {
        var src = LoadHomeRazor();
        // project-paused must be in the project-card-wrap class attribute
        Assert.Matches(new Regex(@"project-card-wrap.*project-paused", RegexOptions.Singleline), src);
        // badge must also be inside (already verified above; re-assert for clarity)
        Assert.Contains("project-card-agent-badge", src);
    }

    // Case 1 & 2: StateHasChanged is called via InvokeAsync (thread-safety requirement)
    [Fact]
    public void Home_CallsInvokeAsyncStateHasChanged_OnRunsChanged()
    {
        var src = LoadHomeRazor();
        Assert.Matches(new Regex(@"InvokeAsync\s*\(\s*StateHasChanged\s*\)"), src);
    }

    // CSS: .project-card-agent-badge rule exists
    [Fact]
    public void AppCss_DefinesProjectCardAgentBadgeClass()
    {
        var css = LoadAppCss();
        Assert.Contains(".project-card-agent-badge", css);
    }
}
