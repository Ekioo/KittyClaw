using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class DashboardRefreshTimingTests
{
    [Fact]
    public void Interval_reports_elapsed_and_remaining_time()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var state = DashboardRefreshTiming.Calculate(
            now,
            now.AddMinutes(-15),
            new TileSidecar("markdown", 3600));

        Assert.Equal(TimeSpan.FromMinutes(15), state.SinceLastRefresh);
        Assert.Equal(TimeSpan.FromMinutes(45), state.UntilNextRefresh);
        Assert.False(state.IsDue);
    }

    [Fact]
    public void Never_refreshed_interval_is_due_now()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var state = DashboardRefreshTiming.Calculate(now, null, new TileSidecar("markdown", 600));

        Assert.Null(state.SinceLastRefresh);
        Assert.Equal(TimeSpan.Zero, state.UntilNextRefresh);
        Assert.True(state.IsDue);
    }

    [Fact]
    public void Static_tile_has_no_next_refresh()
    {
        var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var state = DashboardRefreshTiming.Calculate(
            now,
            now.AddHours(-2),
            new TileSidecar("markdown", 0));

        Assert.Equal(TimeSpan.FromHours(2), state.SinceLastRefresh);
        Assert.Null(state.UntilNextRefresh);
        Assert.False(state.IsDue);
    }
}
