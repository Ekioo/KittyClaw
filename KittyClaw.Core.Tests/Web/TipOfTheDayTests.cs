using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Contract tests for the game-style tip-of-the-day on the unified home: a plain
/// borderless always-visible line in the bottom-right corner (décor, not a dialog —
/// no dismiss controls), one deterministic tip per day, all translated tip pools kept in
/// lockstep with the component's TipCount, and at least one security reminder
/// (local-only tool) in the pool.
/// </summary>
public class TipOfTheDayTests
{
    private static readonly string[] SupportedLanguages = ["en", "fr", "de", "es", "it", "pt-BR", "ja"];
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string ComponentPath() =>
        Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "TipOfTheDay.razor");

    private static string TipsJsonPath(string lang) =>
        Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Tips.{lang}.json");

    private static int DeclaredTipCount()
    {
        var match = Regex.Match(File.ReadAllText(ComponentPath()), @"TipCount\s*=\s*(\d+)");
        Assert.True(match.Success, "TipOfTheDay.razor must declare a TipCount constant.");
        return int.Parse(match.Groups[1].Value);
    }

    private static HashSet<string> Keys(string lang) =>
        Regex.Matches(File.ReadAllText(TipsJsonPath(lang)), "\"([A-Za-z0-9]+)\"\\s*:")
            .Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void TipOfTheDay_IsMountedOnTheUnifiedHome()
    {
        var home = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "UnifiedBoard.razor"));
        Assert.Contains("<TipOfTheDay />", home);
    }

    [Fact]
    public void TipsJson_AllLanguagesKeysMatch()
    {
        foreach (var language in SupportedLanguages.Skip(1))
            Assert.Equal(Keys("en"), Keys(language));
    }

    [Fact]
    public void TipsJson_HasExactlyTipCountTips_InAllLanguages()
    {
        var count = DeclaredTipCount();
        foreach (var lang in SupportedLanguages)
        {
            var keys = Keys(lang);
            for (var i = 1; i <= count; i++)
                Assert.Contains($"Tip{i}", keys);
            Assert.DoesNotContain($"Tip{count + 1}", keys);
        }
    }

    [Fact]
    public void TipsPool_ExplainsTheNewWorkflowFeatures()
    {
        var english = File.ReadAllText(TipsJsonPath("en"));

        Assert.Contains("column processor", english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Routing also protects manual moves", english);
        Assert.Contains("Owner action required", english);
        Assert.Contains("Scheduled tasks", english);
        Assert.Contains("Project skills", english);
        Assert.Contains("migration", english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orange badge", english);
    }

    [Fact]
    public void TipsPool_CarriesTheSecurityReminder()
    {
        // The tips are also the vehicle for the "local tool, no auth, agents run code
        // here" message (chosen over a one-shot banner/onboarding section).
        Assert.Contains("localhost", File.ReadAllText(TipsJsonPath("en")));
        Assert.Contains("localhost", File.ReadAllText(TipsJsonPath("fr")));
    }

    [Fact]
    public void TipOfTheDay_IsDecorNotADialog()
    {
        // Owner direction: plain text blended into the page background, always visible —
        // no dismissal, no persistence, never intercepts clicks. The single allowed
        // control is the ↻ next-tip button, which re-enables its own pointer events.
        var src = File.ReadAllText(ComponentPath());
        Assert.DoesNotContain("localStorage", src);
        Assert.Contains("NextTip", src);
        var css = KittyClaw.Core.Tests.Helpers.AppCssHelper.ReadAll(RepoRoot());
        var tipCss = css[css.IndexOf(".tip-of-the-day", StringComparison.Ordinal)..];
        Assert.Contains("pointer-events: none;", tipCss);
        Assert.Contains("pointer-events: auto;", tipCss);
    }

    [Fact]
    public void TipOfTheDay_BreaksSentencesOntoTheirOwnLines()
    {
        Assert.Contains("TipLines", File.ReadAllText(ComponentPath()));
    }
}
