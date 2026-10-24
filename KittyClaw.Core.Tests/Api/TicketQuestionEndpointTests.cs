using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.Extensions.DependencyInjection;

namespace KittyClaw.Core.Tests.Api;

public sealed partial class McpEndpointTests
{
    [Fact]
    public async Task Questions_round_trip_between_mcp_and_rest_and_resume_only_after_valid_human_answer()
    {
        var slug = await NewProjectAsync();
        var projects = _factory.Services.GetRequiredService<ProjectService>();
        await projects.TogglePauseAsync(slug); // Isolated project must not launch any real provider.
        var columns = _factory.Services.GetRequiredService<ColumnService>();
        var source = await columns.CreateColumnAsync(slug, "Question source");
        var processors = _factory.Services.GetRequiredService<ColumnProcessorService>();
        await processors.SaveAsync(slug, source.Id, "Worker", "Work.", null, true, 20, [], [], []);
        var tickets = _factory.Services.GetRequiredService<TicketService>();
        var ticket = await tickets.CreateTicketAsync(slug, "Need owner decision", status: source.Name, columnId: source.Id);

        var question = await CallAsync("ask_ticket_question", new()
        {
            ["project"] = slug, ["id"] = ticket.Id,
            ["request"] = new { question = "Which scope?", options = new[] { "Code", "Configuration" }, multiSelect = true }
        });
        var questionId = question.GetProperty("id").GetString()!;
        var executionId = question.GetProperty("executionId").GetString()!;
        var questions = await _http.GetFromJsonAsync<List<TicketQuestion>>($"/api/projects/{slug}/tickets/{ticket.Id}/questions");
        Assert.Equal(questionId, Assert.Single(questions!).Id);
        var invalid = await _http.PostAsJsonAsync($"/api/projects/{slug}/tickets/{ticket.Id}/questions/{questionId}/answer",
            new AnswerTicketQuestionRequest(["Invented"]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var answered = await _http.PostAsJsonAsync($"/api/projects/{slug}/tickets/{ticket.Id}/questions/{questionId}/answer",
            new AnswerTicketQuestionRequest(["Code", "Configuration"], "Keep hooks enabled."));
        answered.EnsureSuccessStatusCode();
        var executions = _factory.Services.GetRequiredService<ColumnExecutionService>();
        Assert.Equal(ColumnExecutionStatus.Retrying, (await executions.GetAsync(slug, executionId))!.Status);
        var listed = await CallAsync("list_ticket_questions", new() { ["project"] = slug, ["id"] = ticket.Id });
        Assert.Equal("Keep hooks enabled.", listed[0].GetProperty("freeText").GetString());
        var again = await _http.PostAsJsonAsync($"/api/projects/{slug}/tickets/{ticket.Id}/questions/{questionId}/answer",
            new AnswerTicketQuestionRequest(["Code"]));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var created = await _http.PostAsJsonAsync($"/api/projects/{slug}/tickets/{ticket.Id}/questions",
            new AskTicketQuestionRequest("Proceed?", ["Yes", "No"]));
        created.EnsureSuccessStatusCode();
        var next = (await created.Content.ReadFromJsonAsync<TicketQuestion>())!;
        await CallAsync("answer_ticket_question", new() { ["project"] = slug, ["id"] = ticket.Id,
            ["questionId"] = next.Id, ["request"] = new { selectedOptions = new[] { "Yes" } } });
        Assert.Equal(2, (await tickets.GetTicketAsync(slug, ticket.Id))!.Comments.Count);

        var spec = await _http.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(spec.GetProperty("paths").TryGetProperty("/api/projects/{slug}/tickets/{id}/questions", out _));
    }
}
