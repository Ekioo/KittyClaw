using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnProcessingSuccessGuardTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public async Task Unreadable_refresh_rejects_success_before_configured_actions()
    {
        var fixture = await CreateFixtureAsync("Unreadable engine refresh");
        await using (var db = fixture.Projects.GetProjectDb(fixture.Project.Slug))
        {
            var row = (await db.ColumnExecutions.FindAsync(fixture.Execution.Id))!;
            row.TriggerTicketUpdatedAt = null;
            await db.SaveChangesAsync();
        }

        await fixture.Engine.ProcessAsync(
            fixture.Project.Slug, fixture.Processor, fixture.Execution, CancellationToken.None);

        await AssertRejectedWithoutActionsAsync(fixture, "ticket_refresh_failed");
    }

    [Fact]
    public async Task Mutation_after_final_read_loses_compare_and_swap_without_configured_actions()
    {
        var fixture = await CreateFixtureAsync("Concurrent engine mutation");
        fixture.Executions.BeforeSuccessCompareAndSwapAsync = () => fixture.Tickets.UpdateTicketAsync(
            fixture.Project.Slug, fixture.Ticket.Id, description: "New owner requirement");

        await fixture.Engine.ProcessAsync(
            fixture.Project.Slug, fixture.Processor, fixture.Execution, CancellationToken.None);

        await AssertRejectedWithoutActionsAsync(fixture, "stale_ticket_context");
    }

    [Fact]
    public async Task Non_owner_delivery_with_matching_evidence_uses_custom_success_route_and_real_transition()
    {
        var fixture = await CreateFixtureAsync("Compatible normal delivery", useCustomRoute: true);
        fixture.Dispatcher.Handler = async (projectSlug, _, _, ticket, _) =>
        {
            await fixture.Tickets.AddCommentAsync(projectSlug, ticket.Id, "Fresh delivery evidence", "programmer");
            var refreshed = (await fixture.Tickets.GetTicketAsync(projectSlug, ticket.Id))!;
            return new ColumnAgentResult(
                "shipped", [], "Delivered", Evidence: new ColumnResultEvidence(refreshed.UpdatedAt));
        };
        string? observedFrom = null;
        string? observedTo = null;
        fixture.Tickets.TicketStatusChanged += (_, _, from, to) =>
        {
            observedFrom = from;
            observedTo = to;
        };

        await fixture.Engine.ProcessAsync(
            fixture.Project.Slug, fixture.Processor, fixture.Execution, CancellationToken.None);

        var ticket = (await fixture.Tickets.GetTicketAsync(fixture.Project.Slug, fixture.Ticket.Id))!;
        var attempt = Assert.Single(await fixture.Executions.ListAsync(fixture.Project.Slug, fixture.Ticket.Id));
        Assert.Equal("Success", ticket.Status);
        Assert.Equal("shipped", attempt.Outcome);
        Assert.Null(attempt.ContextRejectionReason);
        Assert.Equal("Work", observedFrom);
        Assert.Equal("Success", observedTo);
        Assert.Contains(ticket.Activities, activity =>
            activity.Text.Contains("(shipped) : Work → Success", StringComparison.Ordinal));
    }

    private static async Task AssertRejectedWithoutActionsAsync(Fixture fixture, string reason)
    {
        var ticket = (await fixture.Tickets.GetTicketAsync(fixture.Project.Slug, fixture.Ticket.Id))!;
        var attempt = Assert.Single(await fixture.Executions.ListAsync(fixture.Project.Slug, fixture.Ticket.Id));
        Assert.Equal(fixture.Source.Id, ticket.ColumnId);
        Assert.Equal(reason, attempt.ContextRejectionReason);
        Assert.Empty(attempt.CompletedActionIds);
        Assert.Empty(ticket.Comments);
        Assert.DoesNotContain(ticket.Activities, activity =>
            activity.Text.Contains("traitement de colonne terminé", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Acceptance_assessments_are_committed_before_routing_only_when_enabled(bool enabled)
    {
        var f = await CreateFixtureAsync("Assessment routing");
        f.Processor.EvaluateAcceptanceCriteria = enabled;
        var criterion = (await f.Tickets.SaveAcceptanceCriterionAsync(f.Project.Slug, f.Ticket.Id, null, new("Works")))!;
        var unrelated = (await f.Tickets.SaveAcceptanceCriterionAsync(f.Project.Slug, f.Ticket.Id, null, new("Unrelated")))!;
        f.Dispatcher.Handler = (_, _, _, ticket, _) => Task.FromResult(new ColumnAgentResult("completed", [],
            Evidence: new(ticket.UpdatedAt), AcceptanceCriteria: [new(criterion.Id, AcceptanceStatus.PASS, new("All checks passed"))]));
        await f.Engine.ProcessAsync(f.Project.Slug, f.Processor, f.Execution, CancellationToken.None);
        var read = (await f.Tickets.GetTicketAsync(f.Project.Slug, f.Ticket.Id))!;
        Assert.Equal(enabled ? AcceptanceStatus.PASS : AcceptanceStatus.Pending, read.AcceptanceCriteria[0].Status);
        Assert.Equal(enabled ? "All checks passed" : null, read.AcceptanceCriteria[0].Evidence?.Content);
        Assert.Equal(unrelated, read.AcceptanceCriteria[1]);
        var execution = (await f.Executions.ListAsync(f.Project.Slug, f.Ticket.Id)).Single();
        Assert.Equal(ColumnExecutionStatus.Completed, execution.Status);
        Assert.NotEqual(f.Source.Id, read.ColumnId);
        if (enabled)
        {
            var assessment = Assert.Single(read.Activities, a => a.Text.Contains("acceptance criterion assessments"));
            Assert.True(assessment.CreatedAt <= execution.EndedAt);
            // Replaying a checkpoint must not overwrite a newer human assessment.
            await f.Tickets.SaveAcceptanceCriterionAsync(f.Project.Slug, f.Ticket.Id, criterion.Id, new("Works", AcceptanceStatus.NeedsRework, new("Owner correction")));
            await f.Executions.SaveAgentResultAsync(f.Project.Slug, f.Execution, f.Execution.AgentResult!, f.Processor);
            Assert.Equal(AcceptanceStatus.ReadyToEvaluate, (await f.Tickets.GetTicketAsync(f.Project.Slug, f.Ticket.Id))!.AcceptanceCriteria[0].Status);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("proof")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    public async Task Invalid_acceptance_reports_leave_criteria_and_routing_unchanged(string scenario)
    {
        var f = await CreateFixtureAsync("Invalid assessments");
        f.Processor.EvaluateAcceptanceCriteria = true;
        var criterion = (await f.Tickets.SaveAcceptanceCriterionAsync(f.Project.Slug, f.Ticket.Id, null, new("Works")))!;
        f.Dispatcher.Handler = async (_, _, _, ticket, _) => {
            if (scenario == "stale") await f.Tickets.UpdateTicketAsync(f.Project.Slug, ticket.Id, description: "New requirement");
            return new ColumnAgentResult("completed", [], Evidence: new(ticket.UpdatedAt),
                AcceptanceCriteria: scenario == "missing" ? null : scenario == "proof" ? [new(criterion.Id, AcceptanceStatus.NeedsRework)] : [
                    new(criterion.Id, AcceptanceStatus.PASS, new("Good proof")),
                    new(scenario == "unknown" ? "unknown-id" : criterion.Id, AcceptanceStatus.NeedsRework,
                        scenario == "proof" ? null : new("More proof"))]);
        };
        await f.Engine.ProcessAsync(f.Project.Slug, f.Processor, f.Execution, CancellationToken.None);
        var read = (await f.Tickets.GetTicketAsync(f.Project.Slug, f.Ticket.Id))!;
        Assert.Equal(criterion, Assert.Single(read.AcceptanceCriteria));
        Assert.Equal(f.Source.Id, read.ColumnId);
        Assert.DoesNotContain(read.Comments, c => c.Content == "success side effect");
        var execution = (await f.Executions.ListAsync(f.Project.Slug, f.Ticket.Id)).Single();
        Assert.False(execution.AgentCompleted);
        Assert.NotEqual(ColumnExecutionStatus.Completed, execution.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_execution_records_evidence_before_routing_and_orders_side_effects_by_policy(bool successTarget)
    {
        var f = await CreateFixtureAsync("Stage ordering", successTarget: successTarget);
        var visited = new List<ColumnExecutionStage>();
        f.Engine.StageChanged = visited.Add;
        f.Dispatcher.Handler = (_, _, _, ticket, _) => Task.FromResult(
            new ColumnAgentResult("completed", [], "Done", Evidence: new(ticket.UpdatedAt)));
        await f.Engine.ProcessAsync(f.Project.Slug, f.Processor, f.Execution, CancellationToken.None);
        Assert.Equal(ColumnExecutionStatus.Completed, (await f.Executions.GetAsync(f.Project.Slug, f.Execution.Id))!.Status);
        Assert.Equal(successTarget, visited.IndexOf(ColumnExecutionStage.Transition) < visited.IndexOf(ColumnExecutionStage.Finalization));
        Assert.True(visited.IndexOf(ColumnExecutionStage.Result) < visited.IndexOf(ColumnExecutionStage.RoutingValidation));
        var ticket = (await f.Tickets.GetTicketAsync(f.Project.Slug, f.Ticket.Id))!;
        Assert.NotEqual(f.Source.Id, ticket.ColumnId);
        Assert.Equal(2, ticket.Comments.Count(c => c.Content == "success side effect"));
    }

    [Fact]
    public async Task Saved_agent_result_resumes_without_dispatch_and_indeterminate_action_stops_before_stages()
    {
        var f = await CreateFixtureAsync("Stage recovery");
        await f.Executions.SaveAgentResultAsync(f.Project.Slug, f.Execution,
            new("completed", [], "Recovered", Evidence: new(f.Ticket.UpdatedAt)), f.Processor);
        f.Dispatcher.Handler = (_, _, _, _, _) => throw new InvalidOperationException("Must not redispatch a checkpoint");
        await f.Engine.ProcessAsync(f.Project.Slug, f.Processor, f.Execution, CancellationToken.None);
        Assert.Equal(ColumnExecutionStatus.Completed, (await f.Executions.GetAsync(f.Project.Slug, f.Execution.Id))!.Status);

        var interrupted = await CreateFixtureAsync("Interrupted stage");
        interrupted.Execution.CurrentActionId = "must-not-run";
        var visited = new List<ColumnExecutionStage>();
        interrupted.Engine.StageChanged = visited.Add;
        await interrupted.Engine.ProcessAsync(interrupted.Project.Slug, interrupted.Processor, interrupted.Execution, CancellationToken.None);
        Assert.Empty(visited);
        Assert.Equal(ColumnExecutionStatus.Failed,
            (await interrupted.Executions.GetAsync(interrupted.Project.Slug, interrupted.Execution.Id))!.Status);
        Assert.Empty((await interrupted.Tickets.GetTicketAsync(interrupted.Project.Slug, interrupted.Ticket.Id))!.Comments);
    }

    [Fact]
    public async Task Rejected_final_transition_never_enters_finalization()
    {
        var f = await CreateFixtureAsync("Stopped transition");
        var visited = new List<ColumnExecutionStage>();
        f.Engine.StageChanged = visited.Add;
        f.Engine.BeforeFinalSuccessValidationAsync = () => f.Tickets.UpdateTicketAsync(
            f.Project.Slug, f.Ticket.Id, description: "Changed after validation");
        await f.Engine.ProcessAsync(f.Project.Slug, f.Processor, f.Execution, CancellationToken.None);
        Assert.Contains(ColumnExecutionStage.Transition, visited);
        Assert.DoesNotContain(ColumnExecutionStage.Finalization, visited);
        Assert.Empty((await f.Tickets.GetTicketAsync(f.Project.Slug, f.Ticket.Id))!.Comments);
    }

    private async Task<Fixture> CreateFixtureAsync(string name, bool useCustomRoute = false, bool successTarget = true)
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync(name);
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var executions = new ColumnExecutionService(projects, tickets);
        var pipeline = await pipelines.CreateAsync(project.Slug, "Delivery");
        var source = await columns.CreateColumnAsync(project.Slug, "Work", pipelineId: pipeline.Id);
        var success = await columns.CreateColumnAsync(
            project.Slug, "Success", pipelineId: pipeline.Id, role: successTarget ? ColumnRole.Success : ColumnRole.Normal);
        var action = new ColumnProcessorAction(
            "must-not-run", new AddCommentActionSpec { Content = "success side effect" });
        var processor = await processors.SaveAsync(
            project.Slug, source.Id, "Worker", "Process.", null, true, 20, [], [], [],
            defaultTargetColumnId: useCustomRoute ? null : success.Id,
            routes: useCustomRoute ? [new ColumnRoute("shipped", success.Id)] : null,
            beforeActions: [action], afterActions: [action with { Id = "after" }]);
        var ticket = await tickets.CreateTicketAsync(
            project.Slug, "Guarded", status: source.Name, pipelineId: pipeline.Id, columnId: source.Id);
        var execution = (await executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow))!;
        var actions = new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance);
        var dispatcher = new CompletedDispatcher();
        var memory = new ColumnMemoryCapitalizationService(projects);
        var engine = new ColumnProcessingEngine(
            projects, tickets, processors, executions, dispatcher, actions, memory,
            NullLogger<ColumnProcessingEngine>.Instance);
        return new(projects, tickets, executions, project, source, processor, ticket, execution, engine, dispatcher);
    }

    public void Dispose() => _temp.Dispose();

    private sealed class CompletedDispatcher : IColumnAgentDispatcher
    {
        public Func<string, ColumnProcessor, ColumnExecution, Ticket, CancellationToken,
            Task<ColumnAgentResult>>? Handler { get; set; }

        public Task<ColumnDispatchResult> DispatchAsync(
            string projectSlug, ColumnProcessor processor, ColumnExecution execution,
            Ticket ticket, CancellationToken cancellationToken) => DispatchCoreAsync(
                projectSlug, processor, execution, ticket, cancellationToken);

        private async Task<ColumnDispatchResult> DispatchCoreAsync(
            string projectSlug, ColumnProcessor processor, ColumnExecution execution,
            Ticket ticket, CancellationToken cancellationToken)
        {
            var result = Handler is null
                ? new ColumnAgentResult("completed", [], "Completed")
                : await Handler(projectSlug, processor, execution, ticket, cancellationToken);
            return new ColumnDispatchResult(
                new AgentRun
                {
                    RunId = execution.Id,
                    ProjectSlug = projectSlug,
                    TicketId = ticket.Id,
                    AgentName = processor.Name,
                    SkillFile = "",
                    ConcurrencyGroup = "test",
                    StartedAt = DateTime.UtcNow,
                },
                result, null);
        }
    }

    private sealed record Fixture(
        ProjectService Projects,
        TicketService Tickets,
        ColumnExecutionService Executions,
        Project Project,
        BoardColumn Source,
        ColumnProcessor Processor,
        Ticket Ticket,
        ColumnExecution Execution,
        ColumnProcessingEngine Engine,
        CompletedDispatcher Dispatcher);
}
