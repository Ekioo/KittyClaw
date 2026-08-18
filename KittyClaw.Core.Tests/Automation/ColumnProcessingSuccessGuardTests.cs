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

    private async Task<Fixture> CreateFixtureAsync(string name, bool useCustomRoute = false)
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
            project.Slug, "Success", pipelineId: pipeline.Id, role: ColumnRole.Success);
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
        var engine = new ColumnProcessingEngine(
            projects, tickets, processors, executions, dispatcher, actions,
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
