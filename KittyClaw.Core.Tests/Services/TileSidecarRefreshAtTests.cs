using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class TileSidecarRefreshAtTests
{
    [Fact]
    public void RoundTrip_IntervalOnly_OmitsRefreshAt()
    {
        var sidecar = new TileSidecar("markdown", 600, "prompt", null, null, RefreshAt: null);
        var yaml = TileSidecarSerializer.Serialize(sidecar);

        Assert.DoesNotContain("refreshAt", yaml);

        var parsed = TileSidecarSerializer.TryParse(yaml);
        Assert.NotNull(parsed);
        Assert.Equal(600, parsed!.Refresh);
        Assert.Null(parsed.RefreshAt);
    }

    [Fact]
    public void RoundTrip_DailyAt_KeepsBothFields()
    {
        var sidecar = new TileSidecar("markdown", 0, "prompt", null, null, RefreshAt: "09:00");
        var yaml = TileSidecarSerializer.Serialize(sidecar);

        Assert.Contains("refreshAt", yaml);

        var parsed = TileSidecarSerializer.TryParse(yaml);
        Assert.NotNull(parsed);
        Assert.Equal(0, parsed!.Refresh);
        Assert.Equal("09:00", parsed.RefreshAt);
    }

    [Fact]
    public void Parse_LegacyYamlWithoutRefreshAt_ReturnsNullRefreshAt()
    {
        var yaml = "template: markdown\nrefresh: 600\nprompt: hi\n";
        var parsed = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(parsed);
        Assert.Equal(600, parsed!.Refresh);
        Assert.Null(parsed.RefreshAt);
    }

    [Theory]
    [InlineData("25:99")]
    [InlineData("abc")]
    [InlineData("9:00")]
    public void Parse_InvalidRefreshAt_TreatedAsNull(string bad)
    {
        var yaml = $"template: markdown\nrefresh: 0\nrefreshAt: \"{bad}\"\n";
        var parsed = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.RefreshAt);
    }

    [Fact]
    public void Parse_EmptyRefreshAt_TreatedAsNull()
    {
        var yaml = "template: markdown\nrefresh: 0\nrefreshAt: \"   \"\n";
        var parsed = TileSidecarSerializer.TryParse(yaml);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.RefreshAt);
    }
}
