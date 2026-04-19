using System.Text.Json;
using Todo.Core.Automation;

namespace Todo.Core.Tests.Automation;

public class ClaudeRunnerFlattenJsonTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public void NonObject_ReturnsToString()
    {
        var result = ClaudeRunner.FlattenJson(Parse("\"hello\""));
        Assert.Equal("hello", result);
    }

    [Fact]
    public void AssistantWithTextContent_ExtractsText()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello world"}]}}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Equal("[assistant] Hello world", result);
    }

    [Fact]
    public void AssistantWithToolUseContent_SkipsToolUse()
    {
        // tool_use parts are emitted as separate tool_use events — FlattenJson must not duplicate them
        var json = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{}}]}}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.DoesNotContain("tool:Read", result);
    }

    [Fact]
    public void AssistantWithTextAndToolUse_OnlyEmitsText()
    {
        var json = """{"type":"assistant","message":{"content":[{"type":"text","text":"Thinking…"},{"type":"tool_use","name":"Bash","input":{}}]}}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Contains("Thinking…", result);
        Assert.DoesNotContain("tool:Bash", result);
    }

    [Fact]
    public void UserWithToolResultArrayContent_ExtractsNestedText()
    {
        var json = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"abc","content":[{"type":"text","text":"result data"}]}]}}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Equal("[user] result data", result);
    }

    [Fact]
    public void UserWithToolResultStringContent_ExtractsString()
    {
        var json = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"abc","content":"plain result"}]}}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Equal("[user] plain result", result);
    }

    [Fact]
    public void EventWithNoExtractableContent_FallsBackToRawJson()
    {
        var json = """{"type":"system","subtype":"init","session_id":"xyz"}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        // No body extracted — falls back to full JSON string
        Assert.Contains("session_id", result);
    }

    [Fact]
    public void DeltaText_Extracted()
    {
        var json = """{"type":"content_block_delta","delta":{"type":"text_delta","text":"streaming text"}}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Equal("[content_block_delta] streaming text", result);
    }

    [Fact]
    public void MessageWithStringContent_Extracted()
    {
        var json = """{"type":"user","message":{"content":"direct string"}}""";
        var result = ClaudeRunner.FlattenJson(Parse(json));
        Assert.Equal("[user] direct string", result);
    }
}
