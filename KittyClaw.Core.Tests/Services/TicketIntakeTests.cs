using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketIntakeTests
{
    [Fact]
    public void QuestionDoesNotRequireTicketFields()
    {
        var reply = TicketIntakeService.Parse("""{"message":"Who uses this?","ready":false}""");
        Assert.False(reply.Ready);
        Assert.Null(reply.Title);
    }

    [Fact]
    public void CompletedInterviewPreservesTicketContent()
    {
        var reply = TicketIntakeService.Parse("""
            ```json
            {"message":"Ready","ready":true,"title":"Allow export","description":"Export the selected rows.\nKeep the active filters."}
            ```
            """);
        Assert.True(reply.Ready);
        Assert.Equal("Allow export", reply.Title);
        Assert.Contains("Keep the active filters.", reply.Description);
    }

    [Fact]
    public void InspectionProgressDoesNotCorruptTheFinalJsonReply()
    {
        var reply = TicketIntakeService.ParseFinalReply([
            "I will inspect the handler for {ticketId} first.",
            "The existing implementation uses { columnId, pipelineId }.",
            """{"message":"The handler already supports this. Which users should have access?","ready":false}""",
            " "
        ]);
        Assert.False(reply.Ready);
        Assert.Contains("Which users", reply.Message);
    }

    [Fact]
    public void MissingFinalReplyCannotCreateATicket()
    {
        Assert.Throws<InvalidOperationException>(() => TicketIntakeService.ParseFinalReply([]));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("{\"message\":\"Ready\",\"ready\":true}")]
    [InlineData("{\"message\":\"Ready\",\"ready\":true,\"title\":\"Title\",\"description\":\" \"}")]
    public void RejectsIncompleteRepliesBeforeCreation(string text)
    {
        Assert.Throws<InvalidOperationException>(() => TicketIntakeService.Parse(text));
    }
}
