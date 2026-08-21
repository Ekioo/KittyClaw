using System.Text.RegularExpressions;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Web;

public sealed class TicketCardIndicatorLayoutTests
{
    private static string RepoRoot() => AppCssHelper.FindRepoRoot();

    [Theory]
    [InlineData("KittyClaw.Web/Components/BoardTicketCard.razor")]
    [InlineData("KittyClaw.Web/Components/Pages/UnifiedBoard.razor")]
    public void CardHeader_SeparatesStatusesFromFixedMetadata(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("ticket-card-statuses", source);
        Assert.Contains("ticket-card-meta", source);
        Assert.Matches(new Regex("ticket-card-statuses[\\s\\S]*owner-action-badge[\\s\\S]*ticket-card-meta[\\s\\S]*ticket-id"), source);
    }

    [Fact]
    public void CardHeader_NarrowCardsDoNotOverlapIndicators()
    {
        var css = AppCssHelper.ReadAll();
        var header = Rule(css, @"\.ticket-card-top");
        var statuses = Rule(css, @"\.ticket-card-statuses");
        var metadata = Rule(css, @"\.ticket-card-meta");
        var action = Rule(css, @"\.owner-action-badge");

        Assert.Contains("min-width: 0", header);
        Assert.Contains("flex-wrap: wrap", statuses);
        Assert.Contains("min-width: 0", statuses);
        Assert.Contains("flex: 0 0 auto", metadata);
        Assert.Contains("overflow: hidden", action);
        Assert.Contains("text-overflow: ellipsis", action);
        Assert.Contains("max-width: 100%", action);
    }

    [Fact]
    public void RunIndicators_NoLongerCompeteForAutomaticMargin()
    {
        var css = AppCssHelper.ReadAll();
        var spinner = Rule(css, @"\.agent-spinner");
        var log = Rule(css, @"\.agent-log-btn");

        Assert.DoesNotContain("margin-left: auto", spinner);
        Assert.DoesNotContain("margin-left: auto", log);
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, selector + @"\s*\{(?<body>[\s\S]*?)\}");
        Assert.True(match.Success, $"CSS rule not found: {selector}");
        return match.Groups["body"].Value;
    }
}
