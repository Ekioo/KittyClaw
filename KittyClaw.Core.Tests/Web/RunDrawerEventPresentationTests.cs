using KittyClaw.Web.Components;

namespace KittyClaw.Core.Tests.Web;

public class RunDrawerEventPresentationTests
{
    private static string RepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (directory is not null && !File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return directory!;
    }

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

    [Fact]
    public void Chat_drawer_sanitizes_and_groups_stderr_events()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ChatDrawer.razor"));

        Assert.Contains("RunDrawerEventPresentation.Sanitize(text)", source);
        Assert.Contains("previous.Role == role", source);
        Assert.Contains("AddDiagnosticMessage(\"stderr\", text);", source);
        Assert.Contains("AddStderrMessage(m.Text)", source);
    }

    [Fact]
    public void Chat_drawer_sanitizes_live_and_historical_error_events()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ChatDrawer.razor"));

        Assert.Contains("else if (m.Role == \"error\") AddErrorMessage(m.Text);", source);
        Assert.Contains("else if (kind == \"error\")", source);
        Assert.Contains("AddErrorMessage(text);", source);
        Assert.Contains("AddDiagnosticMessage(\"error\", text);", source);
    }

    [Fact]
    public void Chat_drawer_collapses_diagnostics_behind_a_friendly_summary()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ChatDrawer.razor"));

        Assert.Contains("msg is DiagnosticMessage diagnostic", source);
        Assert.Contains("<details class=\"chat-diagnostic-block\">", source);
        Assert.Contains("@L[\"ChatTechnicalDetails\"]", source);
        Assert.Contains("text.Contains(\"blocked by policy\"", source);
        Assert.Contains("return L[\"ChatCommandBlocked\"]", source);
    }
}
