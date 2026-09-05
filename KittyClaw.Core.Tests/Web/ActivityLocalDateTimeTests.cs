using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public class ActivityLocalDateTimeTests
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

    [Fact]
    public void Unspecified_database_value_is_treated_as_utc()
    {
        var value = new DateTime(2026, 8, 20, 19, 5, 43, DateTimeKind.Unspecified);

        var normalized = BrowserLocalDateTime.AsUtc(value);

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(value.Ticks, normalized.Ticks);
        Assert.EndsWith("Z", BrowserLocalDateTime.UtcIso(value));
    }

    [Fact]
    public void Local_value_is_converted_to_utc()
    {
        var value = new DateTime(2026, 8, 20, 19, 5, 43, DateTimeKind.Local);

        Assert.Equal(value.ToUniversalTime(), BrowserLocalDateTime.AsUtc(value));
    }

    [Fact]
    public void Activity_timestamps_are_browser_localized_for_comments_and_events()
    {
        var root = RepoRoot();
        var panel = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "TicketPanel.razor"));
        var app = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "App.razor"));
        var script = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "wwwroot", "js", "local-time.js"));

        Assert.Equal(2, panel.Split("<time datetime=").Length - 1);
        Assert.DoesNotContain("@item.At.ToString(\"g\")", panel);
        Assert.Contains("js/local-time.js", app);
        Assert.Equal(2, panel.Split("data-local-date-time-locale=\"@L.Lang\"").Length - 1);
        Assert.Contains("new Intl.DateTimeFormat(locale", script);
        Assert.Contains("new Date(raw)", script);
        Assert.DoesNotContain("timeZone:", script.Split("const localized", 2)[0]);
        Assert.Contains("Number.isNaN(value.getTime())", script);
        Assert.Contains("MutationObserver", script);
        Assert.Contains("data-local-date-time-locale", script);
    }
}
