using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public class VersionCompareTests
{
    [Fact]
    public void IsNewer_returns_true_when_latest_greater_than_current()
    {
        Assert.True(VersionCompare.IsNewer("0.6.0", "v0.7.0"));
    }

    [Fact]
    public void IsNewer_returns_false_when_latest_equals_current()
    {
        Assert.False(VersionCompare.IsNewer("0.6.0", "v0.6.0"));
    }

    [Fact]
    public void IsNewer_returns_false_when_latest_less_than_current()
    {
        Assert.False(VersionCompare.IsNewer("1.0.0", "v0.9.5"));
    }

    [Fact]
    public void IsNewer_handles_tag_without_v_prefix()
    {
        Assert.True(VersionCompare.IsNewer("0.6.0", "0.7.0"));
    }

    [Fact]
    public void IsNewer_returns_false_on_unparseable_tag()
    {
        Assert.False(VersionCompare.IsNewer("0.6.0", "nightly-2026-05-18"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsNewer_returns_false_on_null_or_empty_tag(string? latest)
    {
        Assert.False(VersionCompare.IsNewer("0.6.0", latest));
    }
}
