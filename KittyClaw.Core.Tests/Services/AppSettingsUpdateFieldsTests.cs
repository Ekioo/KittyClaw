using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public class AppSettingsUpdateFieldsTests
{
    [Fact]
    public void UpdateDismissedVersion_and_UpdateCheckLastRun_round_trip_across_reload()
    {
        using var dir = new TempDir();

        var stamp = new DateTime(2026, 5, 18, 12, 34, 56, DateTimeKind.Utc);

        var first = new AppSettingsService(dir.Path);
        first.UpdateDismissedVersion = "0.7.0";
        first.UpdateCheckLastRun = stamp;

        var second = new AppSettingsService(dir.Path);

        Assert.Equal("0.7.0", second.UpdateDismissedVersion);
        Assert.Equal(stamp, second.UpdateCheckLastRun);
    }
}
