using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public class AgentRunnerQuotaSignalTests
{
    private static StreamEvent Ev(string kind, string text, string? detail = null) =>
        new(DateTime.UtcNow, kind, text, detail);

    [Fact]
    public void RateLimitEvent_Rejected_IsQuotaSignal()
    {
        // Shape emitted by the claude CLI when a request is throttled.
        var json = """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"seven_day_sonnet"},"session_id":"abc"}""";
        Assert.True(AgentRunner.IsQuotaSignal(Ev("rate_limit_event", json)));
    }

    [Fact]
    public void RateLimitEvent_Rejected_WithFlattenedPrefix_IsQuotaSignal()
    {
        // Tolerate a flattened "[rate_limit_event] {...}" line, not just raw JSON.
        var json = """[rate_limit_event] {"type":"rate_limit_event","rate_limit_info":{"status":"rejected"}}""";
        Assert.True(AgentRunner.IsQuotaSignal(Ev("rate_limit_event", json)));
    }

    [Fact]
    public void RateLimitEvent_Rejected_InDetailField_IsQuotaSignal()
    {
        // The stdout pump stores raw JSON in Detail while Text is the (lossy) flattened line.
        var json = """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected"}}""";
        Assert.True(AgentRunner.IsQuotaSignal(Ev("rate_limit_event", "[rate_limit_event]", detail: json)));
    }

    [Fact]
    public void ResultEvent_UsageLimitInDetailField_IsQuotaSignal()
    {
        // Text is flattened to "[result]"; the quota message survives only in the raw Detail.
        var json = """{"type":"result","subtype":"success","result":"You've hit your org's monthly usage limit"}""";
        Assert.True(AgentRunner.IsQuotaSignal(Ev("result", "[result]", detail: json)));
    }

    [Fact]
    public void RateLimitEvent_Allowed_IsNotQuotaSignal()
    {
        // The CLI also emits rate_limit_event for non-blocking warnings — must not fall back.
        var json = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning"}}""";
        Assert.False(AgentRunner.IsQuotaSignal(Ev("rate_limit_event", json)));
    }

    [Fact]
    public void ResultEvent_WithUsageLimitText_IsQuotaSignal()
    {
        Assert.True(AgentRunner.IsQuotaSignal(
            Ev("result", "You've hit your org's monthly usage limit")));
    }

    [Fact]
    public void ResultEvent_WithMonthlySpendLimit_IsQuotaSignal()
    {
        // Real Claude CLI wording when the org monthly spend cap is hit (ekioo loop 2026-07).
        Assert.True(AgentRunner.IsQuotaSignal(
            Ev("result", "You've hit your monthly spend limit · raise it at claude.ai/settings/usage")));
    }

    [Fact]
    public void ResultEvent_NormalText_IsNotQuotaSignal()
    {
        Assert.False(AgentRunner.IsQuotaSignal(Ev("result", "Task completed successfully")));
    }

    [Fact]
    public void StderrEvent_WithRateLimit_IsQuotaSignal()
    {
        Assert.True(AgentRunner.IsQuotaSignal(Ev("stderr", "API error: rate_limit_error")));
    }

    [Fact]
    public void AssistantEvent_IsNotInspected()
    {
        // Assistant text is not scanned — agents may legitimately discuss usage limits.
        Assert.False(AgentRunner.IsQuotaSignal(
            Ev("assistant", "[assistant] You've hit your org's monthly usage limit")));
    }
}
