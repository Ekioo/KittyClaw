using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketQuestionTests : IDisposable
{
    private readonly TempDir _temp = new();

    private async Task<Fixture> CreateAsync()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Owner questions");
        var tickets = new TicketService(projects, new MemberService(projects));
        var columns = new ColumnService(projects);
        var source = await columns.CreateColumnAsync(project.Slug, "Ready");
        var target = await columns.CreateColumnAsync(project.Slug, "Review", role: ColumnRole.OwnerAction);
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(project.Slug, source.Id, "Worker", "Work.", null,
            true, 20, [], [], [], defaultTargetColumnId: target.Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Question", status: source.Name, columnId: source.Id);
        var executions = new ColumnExecutionService(projects, tickets);
        var execution = (await executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow))!;
        return new(projects, project.Slug, tickets, columns, processors, processor, ticket, target, executions, execution);
    }

    [Fact]
    public async Task Question_survives_restart_and_answer_reclaims_same_execution_once_with_owner_context()
    {
        var f = await CreateAsync();
        var request = new AskTicketQuestionRequest("Which changes?", ["Code", "Configuration"], true, ExecutionId: f.Execution.Id);
        var question = await f.Executions.AskQuestionAsync(f.Slug, f.Ticket.Id, request);
        Assert.Equal(question.Id, (await f.Executions.AskQuestionAsync(f.Slug, f.Ticket.Id, request)).Id);
        Assert.Equal(ColumnExecutionStatus.WaitingForAnswer, (await f.Executions.GetAsync(f.Slug, f.Execution.Id))!.Status);
        var restarted = new ColumnExecutionService(f.Projects, f.Tickets);
        await restarted.RecoverInterruptedAsync(f.Slug);
        Assert.Null(await restarted.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow));
        Assert.Null(Assert.Single(await restarted.ListQuestionsAsync(f.Slug, f.Ticket.Id)).AnsweredAt);
        await restarted.AnswerQuestionAsync(f.Slug, f.Ticket.Id, question.Id, new(["Code", "Configuration"], "Keep the hooks."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AnswerQuestionAsync(f.Slug, f.Ticket.Id, question.Id, new(["Code"])));
        var claimed = await restarted.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow);
        Assert.Equal(f.Execution.Id, claimed!.Id);
        Assert.Equal(1, claimed.Attempt);
        Assert.False(claimed.AgentCompleted);
        Assert.NotNull(claimed.TriggerOwnerCommentId);
        Assert.Null(await restarted.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow));
        var ticket = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!;
        Assert.Equal(f.Ticket.ColumnId, ticket.ColumnId);
        Assert.Contains("Keep the hooks.", Assert.Single(ticket.Comments).Content);
        var dispatcher = new ColumnAgentDispatcher(null!, f.Projects, null!, null!, null!);
        var context = await dispatcher.BuildDeliveryHistoryAsync(f.Slug, f.Ticket.Id, f.Execution.Id);
        Assert.Contains("Keep the hooks.", context);
        Assert.Contains("Which changes?", context);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("multiple")]
    [InlineData("empty")]
    [InlineData("text")]
    public async Task Invalid_answers_do_not_close_the_question_or_resume(string invalid)
    {
        var f = await CreateAsync();
        var q = await f.Executions.AskQuestionAsync(f.Slug, f.Ticket.Id,
            new("Proceed?", ["Yes", "No"], AllowFreeText: false));
        var request = invalid switch {
            "invalid" => new AnswerTicketQuestionRequest(["Unknown"]),
            "multiple" => new(["Yes", "No"]),
            "text" => new(["Yes"], "Other"),
            _ => new([])
        };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Executions.AnswerQuestionAsync(f.Slug, f.Ticket.Id, q.Id, request));
        Assert.Null(Assert.Single(await f.Executions.ListQuestionsAsync(f.Slug, f.Ticket.Id)).AnsweredAt);
        Assert.Null(await f.Executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow));
    }

    [Fact]
    public async Task Move_or_cancel_closes_pending_question_and_prevents_stale_resume()
    {
        var f = await CreateAsync();
        var q = await f.Executions.AskQuestionAsync(f.Slug, f.Ticket.Id, new("Proceed?", ["Yes", "No"]));
        await f.Tickets.MoveTicketAsync(f.Slug, f.Ticket.Id, f.Target.Name);
        Assert.True(Assert.Single(await f.Executions.ListQuestionsAsync(f.Slug, f.Ticket.Id)).Cancelled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.AnswerQuestionAsync(f.Slug, f.Ticket.Id, q.Id, new(["Yes"])));
    }

    [Fact]
    public async Task Free_text_answer_is_recorded_and_cancellation_closes_the_next_question()
    {
        var f = await CreateAsync();
        var q = await f.Executions.AskQuestionAsync(f.Slug, f.Ticket.Id, new("Proceed?", ["Yes", "No"]));
        var answer = await f.Executions.AnswerQuestionAsync(f.Slug, f.Ticket.Id, q.Id, new([], "Only update tests."));
        Assert.Equal("Only update tests.", answer.FreeText);
        var next = await f.Executions.AskQuestionAsync(f.Slug, f.Ticket.Id, new("Which tests?", ["Unit", "Integration"]));
        Assert.True(await f.Executions.CancelAsync(f.Slug, f.Execution.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.AnswerQuestionAsync(f.Slug, f.Ticket.Id, next.Id, new(["Unit"])));
    }

    [Fact]
    public async Task Late_result_and_failure_cannot_overwrite_waiting_or_answered_question()
    {
        var f = await CreateAsync();
        var q = await f.Executions.AskQuestionAsync(f.Slug, f.Ticket.Id, new("Proceed?", ["Yes", "No"]));
        await f.Executions.FailAttemptAsync(f.Slug, f.Execution, f.Processor, "Late error", "Worker");
        Assert.Equal(ColumnExecutionStatus.WaitingForAnswer, (await f.Executions.GetAsync(f.Slug, f.Execution.Id))!.Status);
        await f.Executions.AnswerQuestionAsync(f.Slug, f.Ticket.Id, q.Id, new(["Yes"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.SaveAgentResultAsync(f.Slug, f.Execution, new("completed", [])));
        await f.Executions.FailAttemptAsync(f.Slug, f.Execution, f.Processor, "Late error", "Worker");
        var execution = (await f.Executions.GetAsync(f.Slug, f.Execution.Id))!;
        Assert.Equal(ColumnExecutionStatus.Retrying, execution.Status);
        Assert.False(execution.AgentCompleted);
        Assert.Null(execution.AgentResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_does_not_fail_or_route_after_question_even_if_answer_arrives_before_run_exits(bool answerImmediately)
    {
        var f = await CreateAsync();
        var dispatcher = new QuestionDispatcher(f.Executions, answerImmediately);
        using var engine = new ColumnProcessingEngine(f.Projects, f.Tickets, f.Processors, f.Executions, dispatcher,
            new ColumnActionExecutor(f.Projects, f.Tickets, NullLogger<ColumnActionExecutor>.Instance),
            new ColumnMemoryCapitalizationService(f.Projects), NullLogger<ColumnProcessingEngine>.Instance);
        await engine.ProcessAsync(f.Slug, f.Processor, f.Execution, CancellationToken.None);
        Assert.Equal(answerImmediately ? ColumnExecutionStatus.Retrying : ColumnExecutionStatus.WaitingForAnswer,
            (await f.Executions.GetAsync(f.Slug, f.Execution.Id))!.Status);
        Assert.Equal(f.Ticket.ColumnId, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        if (!answerImmediately)
        {
            var q = Assert.Single(await f.Executions.ListQuestionsAsync(f.Slug, f.Ticket.Id));
            await f.Executions.AnswerQuestionAsync(f.Slug, f.Ticket.Id, q.Id, new(["Yes"]));
        }
        var retry = (await f.Executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow))!;
        await engine.ProcessAsync(f.Slug, f.Processor, retry, CancellationToken.None);
        Assert.Equal(f.Target.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Equal(ColumnExecutionStatus.Completed, (await f.Executions.GetAsync(f.Slug, f.Execution.Id))!.Status);
        Assert.Equal(2, dispatcher.Count);
    }

    private sealed class QuestionDispatcher(ColumnExecutionService executions, bool answerImmediately) : IColumnAgentDispatcher
    {
        public int Count { get; private set; }
        public async Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Count++;
            if (Count == 1)
            {
                var q = await executions.AskQuestionAsync(slug, ticket.Id, new("Proceed?", ["Yes", "No"], ExecutionId: execution.Id));
                if (answerImmediately) await executions.AnswerQuestionAsync(slug, ticket.Id, q.Id, new(["Yes"]));
            }
            return new(new AgentRun { RunId = execution.Id, ProjectSlug = slug, TicketId = ticket.Id,
                    AgentName = processor.Name, SkillFile = "", ConcurrencyGroup = "test", StartedAt = DateTime.UtcNow },
                Count == 1 ? null : new ColumnAgentResult("completed", [], "Delivered"), "No final JSON");
        }
    }

    public void Dispose() => _temp.Dispose();
    private sealed record Fixture(ProjectService Projects, string Slug, TicketService Tickets, ColumnService Columns,
        ColumnProcessorService Processors, ColumnProcessor Processor, Ticket Ticket, BoardColumn Target,
        ColumnExecutionService Executions, ColumnExecution Execution);
}
