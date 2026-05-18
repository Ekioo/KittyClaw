using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Tests for the UI-friendly frequency mapping helpers used by the dashboard tile config popup.
/// The helpers convert between the persisted sidecar fields (refresh seconds + optional HH:mm)
/// and the UI's (kind, value, time) tuple.
/// </summary>
public sealed class TileFrequencyMappingTests
{
    [Fact]
    public void ToSidecar_Never_ProducesZeroAndNull()
    {
        var (sec, at) = TileFrequency.ToSidecar(TileFrequencyKind.Never, 0, null);
        Assert.Equal(0, sec);
        Assert.Null(at);
    }

    [Theory]
    [InlineData(TileFrequencyKind.Minutes, 10, 600)]
    [InlineData(TileFrequencyKind.Minutes, 1, 60)]
    [InlineData(TileFrequencyKind.Hours, 2, 7200)]
    [InlineData(TileFrequencyKind.Hours, 1, 3600)]
    [InlineData(TileFrequencyKind.Days, 1, 86400)]
    public void ToSidecar_Interval_ConvertsToSeconds(TileFrequencyKind kind, int value, int expectedSeconds)
    {
        var (sec, at) = TileFrequency.ToSidecar(kind, value, null);
        Assert.Equal(expectedSeconds, sec);
        Assert.Null(at);
    }

    [Fact]
    public void ToSidecar_DailyAt_ProducesZeroSecondsAndTimeString()
    {
        var (sec, at) = TileFrequency.ToSidecar(TileFrequencyKind.DailyAt, 0, "09:00");
        Assert.Equal(0, sec);
        Assert.Equal("09:00", at);
    }

    [Fact]
    public void FromSidecar_DailyAt_TakesPrecedenceOverRefreshSeconds()
    {
        var (kind, value, time) = TileFrequency.FromSidecar(new TileSidecar("markdown", 600, "", null, null, "09:00"));
        Assert.Equal(TileFrequencyKind.DailyAt, kind);
        Assert.Equal("09:00", time);
    }

    [Theory]
    [InlineData(600, TileFrequencyKind.Minutes, 10)]
    [InlineData(3600, TileFrequencyKind.Hours, 1)]
    [InlineData(86400, TileFrequencyKind.Days, 1)]
    public void FromSidecar_CleanIntervals_PickExpectedUnit(int seconds, TileFrequencyKind expectedKind, int expectedValue)
    {
        var (kind, value, _) = TileFrequency.FromSidecar(new TileSidecar("markdown", seconds, "", null, null, null));
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void FromSidecar_NonCleanInterval_FallsBackToRoundedMinutes()
    {
        var (kind, value, _) = TileFrequency.FromSidecar(new TileSidecar("markdown", 900, "", null, null, null));
        Assert.Equal(TileFrequencyKind.Minutes, kind);
        Assert.Equal(15, value);
    }

    [Fact]
    public void FromSidecar_ZeroSecondsNoRefreshAt_IsNever()
    {
        var (kind, _, time) = TileFrequency.FromSidecar(new TileSidecar("markdown", 0, "", null, null, null));
        Assert.Equal(TileFrequencyKind.Never, kind);
        Assert.Null(time);
    }
}
