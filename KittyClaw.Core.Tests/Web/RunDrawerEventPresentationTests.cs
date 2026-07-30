using KittyClaw.Web.Components;

namespace KittyClaw.Core.Tests.Web;

public class RunDrawerEventPresentationTests
{
    [Fact]
    public void Group_CoalescesOnlyContiguousStderr()
    {
        var at = DateTime.UtcNow;
        var entries = RunDrawerEventPresentation.Group([
            new(at, "tool_use", "Bash"),
            new(at.AddSeconds(1), "stderr", "first"),
            new(at.AddSeconds(2), "stderr", "second"),
            new(at.AddSeconds(3), "result", "{}"),
            new(at.AddSeconds(4), "stderr", "third"),
        ]);

        Assert.Collection(entries,
            entry => Assert.Equal("tool_use", entry.Event.Kind),
            entry =>
            {
                Assert.Equal(2, entry.Stderr!.EventCount);
                Assert.Equal("first\nsecond", entry.Stderr.Raw);
            },
            entry => Assert.Equal("result", entry.Event.Kind),
            entry => Assert.Equal("third", entry.Stderr!.Raw));
    }

    [Fact]
    public void Group_CapsPreviewButRetainsCompleteRawStderr()
    {
        var raw = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"diagnostic line {i}: {new string('x', 100)}"));

        var stderr = Assert.Single(RunDrawerEventPresentation.Group([
            new StreamEvent(DateTime.UtcNow, "stderr", raw),
        ])).Stderr!;

        Assert.True(stderr.IsTruncated);
        Assert.EndsWith("\n…", stderr.Preview);
        Assert.True(stderr.Preview.Length <= 1202);
        Assert.Equal(raw, stderr.Raw);
    }

    [Fact]
    public void Group_StripsAnsiAndControlsFromPreviewAndRetainedRaw()
    {
        var stderr = Assert.Single(RunDrawerEventPresentation.Group([
            new StreamEvent(DateTime.UtcNow, "stderr", "\u001b[31mBad\u001b[0m\u0000\tvalue"),
        ])).Stderr!;

        Assert.Equal("Bad\tvalue", stderr.Raw);
        Assert.Equal("Bad\tvalue", stderr.Preview);
    }

    [Theory]
    [InlineData("System.ArgumentException: required query parameter 'enabled' was not provided", "System.ArgumentException: required query parameter 'enabled' was not provided")]
    [InlineData("<h1>Bad request</h1>\n<div>Microsoft.AspNetCore.Http.BadHttpRequestException: Missing bool flag</div>", "Microsoft.AspNetCore.Http.BadHttpRequestException: Missing bool flag")]
    [InlineData("large unrelated payload", "Command failed")]
    public void ExtractSummary_PrefersExceptionAndHasNeutralFallback(string raw, string expected)
    {
        Assert.Equal(expected, RunDrawerEventPresentation.ExtractSummary(raw));
    }
}
