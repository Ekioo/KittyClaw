using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketIntakeTests
{
    [Fact]
    public async Task AssistedFieldsArePersistedTogetherOnCreation()
    {
        using var tmp = new KittyClaw.Core.Tests.Helpers.TempDir();
        var projects = new KittyClaw.Core.Services.ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("Intake regression");
        var labels = new KittyClaw.Core.Services.LabelService(projects);
        var label = await labels.CreateLabelAsync(project.Slug, "Interface", "#123456");
        var reply = TicketIntakeService.Parse($$"""
            {"message":"Prêt","ready":true,"title":"Exporter les données","description":"Conserver les filtres.","acceptanceCriteria":["L’export conserve les filtres."],"labelIds":[{{label.Id}}]}
            """);
        var tickets = new KittyClaw.Core.Services.TicketService(projects, new KittyClaw.Core.Services.MemberService(projects));
        var created = await tickets.CreateTicketAsync(project.Slug, reply.Title!, reply.Description!,
            labelIds: reply.LabelIds, acceptanceCriteria: reply.AcceptanceCriteria);
        var saved = await tickets.GetTicketAsync(project.Slug, created.Id);
        Assert.NotNull(saved);
        Assert.Equal("Exporter les données", saved.Title);
        Assert.Equal("Conserver les filtres.", saved.Description);
        Assert.Equal(label.Id, Assert.Single(saved.Labels).Id);
        var criterion = Assert.Single(saved.AcceptanceCriteria);
        Assert.Equal("L’export conserve les filtres.", criterion.Text);
        Assert.Equal(KittyClaw.Core.Models.AcceptanceStatus.Pending, criterion.Status);
        Assert.Null(criterion.Evidence);
    }

    [Fact]
    public void RejectsEmptyStructuredCriterion()
    {
        Assert.Throws<InvalidOperationException>(() => TicketIntakeService.Parse("""
            {"message":"Ready","ready":true,"title":"Export","description":"Export rows","acceptanceCriteria":[" "]}
            """));
    }

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
