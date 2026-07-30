using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public class AgentRunnerModelUnavailableSignalTests
{
    private static StreamEvent Ev(string kind, string text, string? detail = null) =>
        new(DateTime.UtcNow, kind, text, detail);

    [Fact]
    public void ClaudeResult_InaccessibleSelectedModel_IsDetected()
    {
        var json = """
            {"type":"result","subtype":"success","is_error":true,"api_error_status":404,"result":"There's an issue with the selected model (gemma4). It may not exist or you may not have access to it. Run --model to pick a different model."}
            """;

        Assert.True(AgentRunner.IsModelUnavailableSignal(
            Ev("result", "[result]", detail: json)));
    }

    [Fact]
    public void Stderr_ModelNotFound_IsDetected()
    {
        Assert.True(AgentRunner.IsModelUnavailableSignal(
            Ev("stderr", "API error: model not found")));
    }

    [Fact]
    public void UnrelatedHttp404_IsNotDetected()
    {
        Assert.False(AgentRunner.IsModelUnavailableSignal(
            Ev("error", "HTTP 404: ticket endpoint not found")));
    }

    [Fact]
    public void AssistantDiscussion_IsNotInspected()
    {
        Assert.False(AgentRunner.IsModelUnavailableSignal(
            Ev("assistant", "The selected model may not exist.")));
    }

    [Fact]
    public void NormalResult_IsNotDetected()
    {
        Assert.False(AgentRunner.IsModelUnavailableSignal(
            Ev("result", "Task completed successfully")));
    }
}
