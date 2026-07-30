using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

/// <summary>
/// Contract tests for the game-style tip-of-the-day on the unified home: one tip per
/// day per browser in a quiet fixed corner, next / hide-today / never-again controls,
/// en/fr tip pools kept in lockstep with the component's TipCount, and at least one
/// security reminder (local-only tool) in the pool.
/// </summary>
public class TipOfTheDayTests
{
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
    public void TipsJson_EnAndFrKeysMatch()
    {
        Assert.Equal(Keys("en"), Keys("fr"));
    }

    [Fact]
    public void TipsJson_HasExactlyTipCountTips_InBothLanguages()
    {
        var count = DeclaredTipCount();
        foreach (var lang in new[] { "en", "fr" })
        {
            var keys = Keys(lang);
            for (var i = 1; i <= count; i++)
                Assert.Contains($"Tip{i}", keys);
            Assert.DoesNotContain($"Tip{count + 1}", keys);
        }
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
    public void TipOfTheDay_PersistsDismissalsClientSide()
    {
        var src = File.ReadAllText(ComponentPath());
        Assert.Contains("kc-tips-disabled", src);
        Assert.Contains("kc-tip-hidden-on", src);
    }
}
