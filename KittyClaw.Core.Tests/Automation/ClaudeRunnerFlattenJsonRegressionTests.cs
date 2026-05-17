using System.Text.Json;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

// Regression tests for the FlattenJson fallback path broken by fb4c6ab/89e7025.
// When a Claude event has a `type` field but no extractable body, FlattenJson must
// return the raw JSON string, not just the type prefix.
public class ClaudeRunnerFlattenJsonRegressionTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SystemEvent_FallbackIncludesAllFields()
    {
        // Regression: previously returned "[system] " — raw JSON must be returned instead
        var json = """{"type":"system","subtype":"init","session_id":"abc-123"}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Contains("session_id", result);
        Assert.Contains("subtype", result);
    }

    [Fact]
    public void SystemEvent_FallbackIsNotJustTypePrefix()
    {
        var json = """{"type":"system","subtype":"init","session_id":"abc-123"}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.NotEqual("[system] ", result);
    }

    [Fact]
    public void UnknownTypeWithExtraFields_FallsBackToRawJson()
    {
        var json = """{"type":"ping","payload":"heartbeat"}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Contains("payload", result);
    }
}
