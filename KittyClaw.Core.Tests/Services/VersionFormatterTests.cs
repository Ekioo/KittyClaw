using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Services;

public class VersionFormatterTests
{
    [Fact]
    public void Format_release_version_returns_three_part_string()
    {
        Assert.Equal("0.7.0", VersionFormatter.Format("0.7.0"));
    }

    [Fact]
    public void Format_pre_release_strips_prerelease_and_build_metadata()
    {
        Assert.Equal("0.7.1", VersionFormatter.Format("0.7.1-alpha.0.3+abc123"));
    }

    [Fact]
    public void Format_strips_prerelease_only()
    {
        Assert.Equal("0.7.1", VersionFormatter.Format("0.7.1-alpha.0.3"));
    }

    [Fact]
    public void Format_strips_build_metadata_only()
    {
        Assert.Equal("0.7.0", VersionFormatter.Format("0.7.0+abc123"));
    }

    [Fact]
    public void Format_null_falls_back_to_zero()
    {
        Assert.Equal("0.0.0", VersionFormatter.Format(null));
    }

    [Fact]
    public void Format_empty_falls_back_to_zero()
    {
        Assert.Equal("0.0.0", VersionFormatter.Format(""));
    }

    [Fact]
    public void Format_minver_fallback_when_no_tags_returns_zero_baseline()
    {
        // MinVer emits "0.0.0-alpha.0.<height>" when there is no matching tag
        // (e.g. tag missing the `v` prefix, or fresh clone with no tags).
        // The formatter must still return a parseable 3-part version, never throw.
        Assert.Equal("0.0.0", VersionFormatter.Format("0.0.0-alpha.0.5"));
    }
}
