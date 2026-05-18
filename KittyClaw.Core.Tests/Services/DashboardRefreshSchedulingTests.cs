using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Pure-function scheduling tests for the time-of-day branch of <see cref="DashboardRefreshService"/>.
/// </summary>
public sealed class DashboardRefreshSchedulingTests
{
    [Fact]
    public void DailyAt_FiresWhenTodayPassedAndLastRefreshWasYesterday()
    {
        var now = new DateTime(2026, 5, 19, 9, 30, 0, DateTimeKind.Local);
        var lastRefresh = new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Local);

        Assert.True(DashboardRefreshScheduling.ShouldFireDailyAt(now, lastRefresh, "09:00"));
    }

    [Fact]
    public void DailyAt_FiresWhenLastRefreshIsNullAndTimeHasPassedToday()
    {
        var now = new DateTime(2026, 5, 19, 9, 30, 0, DateTimeKind.Local);

        Assert.True(DashboardRefreshScheduling.ShouldFireDailyAt(now, null, "09:00"));
    }

    [Fact]
    public void DailyAt_DoesNotFireWhenAlreadyFiredToday()
    {
        var now = new DateTime(2026, 5, 19, 14, 0, 0, DateTimeKind.Local);
        var lastRefresh = new DateTime(2026, 5, 19, 9, 5, 0, DateTimeKind.Local);

        Assert.False(DashboardRefreshScheduling.ShouldFireDailyAt(now, lastRefresh, "09:00"));
    }

    [Fact]
    public void DailyAt_DoesNotFireBeforeTargetTime()
    {
        var now = new DateTime(2026, 5, 19, 8, 59, 0, DateTimeKind.Local);

        Assert.False(DashboardRefreshScheduling.ShouldFireDailyAt(now, null, "09:00"));
    }

    [Fact]
    public void DailyAt_InvalidTimeString_DoesNotFire()
    {
        var now = new DateTime(2026, 5, 19, 23, 59, 0, DateTimeKind.Local);

        Assert.False(DashboardRefreshScheduling.ShouldFireDailyAt(now, null, "25:99"));
        Assert.False(DashboardRefreshScheduling.ShouldFireDailyAt(now, null, ""));
        Assert.False(DashboardRefreshScheduling.ShouldFireDailyAt(now, null, null));
    }
}
