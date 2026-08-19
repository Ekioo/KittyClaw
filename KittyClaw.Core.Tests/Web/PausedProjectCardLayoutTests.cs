using System.Text.RegularExpressions;
using KittyClaw.Core.Tests.Helpers;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

public sealed class PausedProjectCardLayoutTests
{
    private static string LoadComponent() => File.ReadAllText(Path.Combine(
        AppCssHelper.FindRepoRoot(), "KittyClaw.Web", "Components", "ProjectCards.razor"));

    [Fact]
    public void PausedCard_SeparatesHeadingStatusAndResumeAction()
    {
        var source = LoadComponent();

        Assert.Contains("project-card-heading", source);
        Assert.Contains("project-paused-badge", source);
        Assert.Contains("project-pause-btn-label", source);
        Assert.Contains("L[\"Resume\"]", source);
    }

    [Fact]
    public void PausedCard_RemainsReadableAndUsesBottomResumePill()
    {
        var css = AppCssHelper.ReadAll();
        var card = Rule(css, @"\.project-card-wrap\.project-paused \.project-card");
        var action = Rule(css, @"\.project-card-wrap\.project-paused \.project-pause-btn");

        Assert.DoesNotContain("opacity", card, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("padding-bottom: 3rem", card);
        Assert.Contains("bottom: 0.65rem", action);
        Assert.Contains("border-radius: 999px", action);
        Assert.Contains("opacity: 1", action);
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, selector + @"\s*\{(?<body>[\s\S]*?)\}");
        Assert.True(match.Success, $"CSS rule not found: {selector}");
        return match.Groups["body"].Value;
    }
}
