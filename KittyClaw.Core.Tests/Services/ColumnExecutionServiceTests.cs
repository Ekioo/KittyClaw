using KittyClaw.Core.Models;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class ColumnExecutionServiceTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ProjectService _projects;
    private readonly PipelineService _pipelines;
    private readonly ColumnService _columns;
    private readonly TicketService _tickets;
    private readonly ProjectSkillService _skills;
    private readonly ColumnProcessorService _processors;
    private readonly ColumnExecutionService _executions;

    public ColumnExecutionServiceTests()
    {
        _projects = new ProjectService(_temp.Path);
        _pipelines = new PipelineService(_projects);
        _columns = new ColumnService(_projects);
        _tickets = new TicketService(_projects, new MemberService(_projects));
        _skills = new ProjectSkillService(_projects);
        _processors = new ColumnProcessorService(_projects, _skills);
        _executions = new ColumnExecutionService(_projects, _tickets);
    }

    [Fact]
    public async Task Claim_uses_configured_order_without_moving_ticket_to_in_progress()
    {
        var project = await _projects.CreateProjectAsync("Priority queue");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Delivery");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var low = await _tickets.CreateTicketAsync(project.Slug, "Low", status: source.Name,
            priority: TicketPriority.Idea, pipelineId: pipeline.Id, columnId: source.Id);
        var high = await _tickets.CreateTicketAsync(project.Slug, "High", status: source.Name,
            priority: TicketPriority.Critical, pipelineId: pipeline.Id, columnId: source.Id);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id,
            selectionOrder: TicketSelectionOrder.PriorityThenPosition);

        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        var reloaded = await _tickets.GetTicketAsync(project.Slug, high.Id);

        Assert.NotNull(execution);
        Assert.Equal(high.Id, execution.TicketId);
        Assert.Equal(source.Id, reloaded!.ColumnId);
        Assert.Equal(source.Name, reloaded.Status);
        Assert.NotEqual(low.Id, execution.TicketId);
    }

    [Fact]
    public async Task Claim_migrates_legacy_ticket_cost_schema_before_querying()
    {
        var project = await _projects.CreateProjectAsync("Legacy ticket cost schema");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Delivery");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        await using (var db = _projects.GetProjectDb(project.Slug))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tickets DROP COLUMN AgentCostEstimated");

        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        Assert.Null(execution);
        await using var migrated = _projects.GetProjectDb(project.Slug);
        var columns = await migrated.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Tickets')").ToListAsync();
        Assert.Contains("AgentCostEstimated", columns);
    }

    [Fact]
    public async Task Completion_routes_by_outcome_across_pipelines_using_stable_column_ids()
    {
        var project = await _projects.CreateProjectAsync("Routing");
        var intake = await _pipelines.CreateAsync(project.Slug, "Intake");
        var delivery = await _pipelines.CreateAsync(project.Slug, "Delivery");
        var source = await _columns.CreateColumnAsync(project.Slug, "Assess", pipelineId: intake.Id);
        var accepted = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: delivery.Id,
            role: ColumnRole.OwnerAction);
        var rejected = await _columns.CreateColumnAsync(project.Slug, "Rejected", pipelineId: intake.Id, role: ColumnRole.Failure);
        var skill = await _skills.CreateAsync(project.Slug, "Validate", "Validate the ticket.");
        var processor = await _processors.SaveAsync(project.Slug, source.Id, "Assessor", "Assess.", null,
            true, 20, [skill.Slug], [skill.Slug], [skill.Slug],
            defaultTargetColumnId: rejected.Id, routes: [new("accepted", accepted.Id)]);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Candidate", status: source.Name,
            pipelineId: intake.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("accepted", [skill.Slug], "Valid."), "column-agent");
        var moved = await _tickets.GetTicketAsync(project.Slug, ticket.Id);

        Assert.Equal(delivery.Id, moved!.PipelineId);
        Assert.Equal(accepted.Id, moved.ColumnId);
        Assert.Equal("Ready", moved.Status);
        Assert.Equal("owner", moved.AssignedTo);
        Assert.Equal(ColumnExecutionStatus.Completed,
            Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id)).Status);
    }

    [Fact]
    public async Task Scheduled_completion_persists_wake_atomically_with_waiting_route()
    {
        var project = await _projects.CreateProjectAsync("Scheduled route");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Distribution");
        var source = await _columns.CreateColumnAsync(project.Slug, "À traiter", pipelineId: pipeline.Id);
        var waiting = await _columns.CreateColumnAsync(project.Slug, "Planifié", pipelineId: pipeline.Id, role: ColumnRole.Waiting);
        var processor = await SaveProcessor(project.Slug, source.Id, waiting.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Future publication", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        var fireAt = new DateTime(2026, 8, 8, 8, 0, 0, DateTimeKind.Utc);

        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("scheduled", [], "Publication programmée.", fireAt, source.Name), "column-agent");
        var scheduled = await _tickets.GetTicketAsync(project.Slug, ticket.Id);

        Assert.Equal(waiting.Id, scheduled!.ColumnId);
        Assert.Equal(fireAt, scheduled.FireAt);
        Assert.Equal(source.Name, scheduled.ScheduleTarget);
        Assert.Equal(ColumnExecutionStatus.Completed,
            Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id)).Status);
    }

    [Fact]
    public async Task Scheduled_completion_without_date_is_rejected_before_routing()
    {
        var project = await _projects.CreateProjectAsync("Invalid scheduled route");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Distribution");
        var source = await _columns.CreateColumnAsync(project.Slug, "À traiter", pipelineId: pipeline.Id);
        var waiting = await _columns.CreateColumnAsync(project.Slug, "Planifié", pipelineId: pipeline.Id, role: ColumnRole.Waiting);
        var processor = await SaveProcessor(project.Slug, source.Id, waiting.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Missing wake", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("scheduled", [], "No date", ScheduleTarget: source.Name), "column-agent");
        var unchanged = await _tickets.GetTicketAsync(project.Slug, ticket.Id);
        var attempt = Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id));

        Assert.Equal(source.Id, unchanged!.ColumnId);
        Assert.Null(unchanged.FireAt);
        Assert.Null(unchanged.ScheduleTarget);
        Assert.Equal(ColumnExecutionStatus.Retrying, attempt.Status);
        Assert.Contains("fireAt", attempt.Error);
    }

    [Fact]
    public async Task Scheduled_completion_accepts_a_renamed_waiting_column()
    {
        var project = await _projects.CreateProjectAsync("Renamed scheduled destination");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Distribution");
        var source = await _columns.CreateColumnAsync(project.Slug, "À traiter", pipelineId: pipeline.Id);
        var waiting = await _columns.CreateColumnAsync(project.Slug, "Programmé", pipelineId: pipeline.Id, role: ColumnRole.Waiting);
        var processor = await SaveProcessor(project.Slug, source.Id, waiting.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Future publication", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        var fireAt = DateTime.UtcNow.AddDays(1);

        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("scheduled", [], "Later.", fireAt, source.Name), "column-agent");
        var scheduled = await _tickets.GetTicketAsync(project.Slug, ticket.Id);
        var attempt = Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id));

        Assert.Equal(waiting.Id, scheduled!.ColumnId);
        Assert.Equal(fireAt, scheduled.FireAt);
        Assert.Equal(source.Name, scheduled.ScheduleTarget);
        Assert.Equal(ColumnExecutionStatus.Completed, attempt.Status);
    }

    [Fact]
    public async Task Scheduled_completion_to_a_non_waiting_column_is_rejected()
    {
        var project = await _projects.CreateProjectAsync("Invalid scheduled destination");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Distribution");
        var source = await _columns.CreateColumnAsync(project.Slug, "À traiter", pipelineId: pipeline.Id);
        var normal = await _columns.CreateColumnAsync(project.Slug, "Programmé", pipelineId: pipeline.Id, role: ColumnRole.Normal);
        var processor = await SaveProcessor(project.Slug, source.Id, normal.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Future publication", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("scheduled", [], "Later.", DateTime.UtcNow.AddDays(1), source.Name), "column-agent");
        var unchanged = await _tickets.GetTicketAsync(project.Slug, ticket.Id);
        var attempt = Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id));

        Assert.Equal(source.Id, unchanged!.ColumnId);
        Assert.Null(unchanged.FireAt);
        Assert.Equal(ColumnExecutionStatus.Retrying, attempt.Status);
        Assert.Contains("rôle Attente", attempt.Error);
    }

    [Fact]
    public async Task Needs_input_waiting_route_does_not_create_a_wake()
    {
        var project = await _projects.CreateProjectAsync("External wait");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Distribution");
        var source = await _columns.CreateColumnAsync(project.Slug, "À traiter", pipelineId: pipeline.Id);
        var waiting = await _columns.CreateColumnAsync(project.Slug, "En attente", pipelineId: pipeline.Id, role: ColumnRole.Waiting);
        var processor = await SaveProcessor(project.Slug, source.Id, waiting.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Owner approval", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("needs_input", [], "Approval required."), "column-agent");
        var parked = await _tickets.GetTicketAsync(project.Slug, ticket.Id);

        Assert.Equal(waiting.Id, parked!.ColumnId);
        Assert.Null(parked.FireAt);
        Assert.Null(parked.ScheduleTarget);
    }

    [Fact]
    public async Task Technical_failure_retries_then_routes_and_releases_ticket()
    {
        var project = await _projects.CreateProjectAsync("Retries");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var failures = await _columns.CreateColumnAsync(project.Slug, "Technical failure", pipelineId: pipeline.Id, role: ColumnRole.Failure);
        var processor = await SaveProcessor(project.Slug, source.Id, failures.Id, maxAttempts: 2,
            technicalFailureColumnId: failures.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Fragile", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var first = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        await _executions.FailAttemptAsync(project.Slug, first!, processor, "timeout", "column-agent");
        var retry = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow.AddMinutes(1));
        await _executions.FailAttemptAsync(project.Slug, retry!, processor, "timeout again", "column-agent");

        var moved = await _tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.Equal(failures.Id, moved!.ColumnId);
        Assert.Equal(ColumnExecutionStatus.Completed,
            Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id)).Status);
    }

    [Fact]
    public async Task Parent_waits_for_blocking_children_then_becomes_eligible()
    {
        var project = await _projects.CreateProjectAsync("Children");
        var parentPipeline = await _pipelines.CreateAsync(project.Slug, "Parent flow");
        var childPipeline = await _pipelines.CreateAsync(project.Slug, "Child flow");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: parentPipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: parentPipeline.Id, role: ColumnRole.Success);
        var childWork = await _columns.CreateColumnAsync(project.Slug, "Work", pipelineId: childPipeline.Id);
        var childDone = await _columns.CreateColumnAsync(project.Slug, "Validated", pipelineId: childPipeline.Id, role: ColumnRole.Success);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        var parent = await _tickets.CreateTicketAsync(project.Slug, "Parent", status: source.Name,
            pipelineId: parentPipeline.Id, columnId: source.Id);
        var child = await _tickets.CreateTicketAsync(project.Slug, "Child", status: childWork.Name,
            parentId: parent.Id, pipelineId: childPipeline.Id, columnId: childWork.Id, blocksParent: true);

        Assert.Null(await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow));
        await _tickets.UpdateTicketAsync(project.Slug, child.Id, status: childDone.Name);

        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        Assert.NotNull(execution);
        Assert.Equal(parent.Id, execution.TicketId);
    }

    [Fact]
    public async Task Waiting_parent_resumes_when_multiple_children_share_the_same_success_column()
    {
        var project = await _projects.CreateProjectAsync("Shared child success column");
        var parentPipeline = await _pipelines.CreateAsync(project.Slug, "Parent flow");
        var childPipeline = await _pipelines.CreateAsync(project.Slug, "Child flow");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: parentPipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: parentPipeline.Id, role: ColumnRole.Success);
        var childWork = await _columns.CreateColumnAsync(project.Slug, "Work", pipelineId: childPipeline.Id);
        var childDone = await _columns.CreateColumnAsync(project.Slug, "Validated", pipelineId: childPipeline.Id, role: ColumnRole.Success);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        var parent = await _tickets.CreateTicketAsync(project.Slug, "Parent", status: source.Name,
            pipelineId: parentPipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        var firstChild = await _tickets.CreateTicketAsync(project.Slug, "First child", status: childWork.Name,
            parentId: parent.Id, pipelineId: childPipeline.Id, columnId: childWork.Id, blocksParent: true);
        var secondChild = await _tickets.CreateTicketAsync(project.Slug, "Second child", status: childWork.Name,
            parentId: parent.Id, pipelineId: childPipeline.Id, columnId: childWork.Id, blocksParent: true);

        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("wait_for_children", []), "column-agent");
        await _tickets.UpdateTicketAsync(project.Slug, firstChild.Id, status: childDone.Name);
        await _tickets.UpdateTicketAsync(project.Slug, secondChild.Id, status: childDone.Name);

        var resumed = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        Assert.NotNull(resumed);
        Assert.Equal(execution!.Id, resumed.Id);
        Assert.Equal(parent.Id, resumed.TicketId);
    }

    [Fact]
    public async Task Waiting_parent_resumes_when_its_last_child_stops_blocking()
    {
        var project = await _projects.CreateProjectAsync("Removed child blocker");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var childWork = await _columns.CreateColumnAsync(project.Slug, "Child work", pipelineId: pipeline.Id);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        var parent = await _tickets.CreateTicketAsync(project.Slug, "Parent", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        var child = await _tickets.CreateTicketAsync(project.Slug, "Child", status: childWork.Name,
            parentId: parent.Id, pipelineId: pipeline.Id, columnId: childWork.Id, blocksParent: true);
        await _executions.CompleteAsync(project.Slug, execution!, processor,
            new ColumnAgentResult("wait_for_children", []), "column-agent");

        await _tickets.UpdateTicketAsync(project.Slug, child.Id, blocksParent: false);

        var resumed = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        Assert.NotNull(resumed);
        Assert.Equal(execution!.Id, resumed.Id);
        Assert.Equal(parent.Id, resumed.TicketId);
    }

    [Fact]
    public async Task Unclaimed_parent_is_eligible_when_multiple_children_share_the_same_success_column()
    {
        var project = await _projects.CreateProjectAsync("Already successful children");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        var parent = await _tickets.CreateTicketAsync(project.Slug, "Parent", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        await _tickets.CreateTicketAsync(project.Slug, "First child", status: target.Name,
            parentId: parent.Id, pipelineId: pipeline.Id, columnId: target.Id, blocksParent: true);
        await _tickets.CreateTicketAsync(project.Slug, "Second child", status: target.Name,
            parentId: parent.Id, pipelineId: pipeline.Id, columnId: target.Id, blocksParent: true);

        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        Assert.NotNull(execution);
        Assert.Equal(parent.Id, execution.TicketId);
    }

    [Fact]
    public async Task Non_blocking_child_does_not_hold_parent()
    {
        var project = await _projects.CreateProjectAsync("Informational child");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var childWork = await _columns.CreateColumnAsync(project.Slug, "Child work", pipelineId: pipeline.Id);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        var parent = await _tickets.CreateTicketAsync(project.Slug, "Parent", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var child = await _tickets.CreateTicketAsync(project.Slug, "Note", status: childWork.Name,
            parentId: parent.Id, pipelineId: pipeline.Id, columnId: childWork.Id);

        await _tickets.UpdateTicketAsync(project.Slug, child.Id, blocksParent: false);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        Assert.NotNull(execution);
        Assert.Equal(parent.Id, execution.TicketId);
    }

    [Fact]
    public async Task Recovering_interrupted_execution_does_not_consume_an_attempt()
    {
        var project = await _projects.CreateProjectAsync("Restart recovery");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        await _tickets.CreateTicketAsync(project.Slug, "Interrupted", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var first = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        await _executions.RecoverInterruptedAsync(project.Slug);
        var resumed = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        Assert.NotNull(first);
        Assert.NotNull(resumed);
        Assert.Equal(first.Id, resumed.Id);
        Assert.Equal(1, resumed.Attempt);
    }

    [Fact]
    public async Task Action_and_agent_checkpoints_survive_a_restart_without_replaying_successes()
    {
        var project = await _projects.CreateProjectAsync("Durable action checkpoint");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        await _tickets.CreateTicketAsync(project.Slug, "Checkpoint", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        await _executions.BeginActionAsync(project.Slug, execution!, "prepare");
        await _executions.CompleteActionAsync(project.Slug, execution!, "prepare");
        var result = new ColumnAgentResult("approved", ["skill"], "done");
        await _executions.SaveAgentResultAsync(project.Slug, execution!, result);
        await _executions.RecoverInterruptedAsync(project.Slug);
        var resumed = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);

        Assert.NotNull(resumed);
        Assert.Contains("prepare", resumed.CompletedActionIds);
        Assert.Null(resumed.CurrentActionId);
        Assert.True(resumed.AgentCompleted);
        Assert.Equal("approved", resumed.AgentResult!.Outcome);
    }

    [Fact]
    public async Task Action_failure_route_is_terminal_and_cannot_reclaim_the_same_column()
    {
        var project = await _projects.CreateProjectAsync("Action failure route");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var failure = await _columns.CreateColumnAsync(project.Slug, "Technical failure", pipelineId: pipeline.Id, role: ColumnRole.Failure);
        var processor = await SaveProcessor(project.Slug, source.Id, failure.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Fragile action", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        var action = new ColumnProcessorAction(
            "publish", new HttpRequestActionSpec { Url = "https://example.com" }, failure.Id);

        await _executions.RouteActionFailureAsync(
            project.Slug, execution!, processor, action, "HTTP 500", "worker");

        var moved = await _tickets.GetTicketAsync(project.Slug, ticket.Id);
        var history = Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id));
        Assert.Equal(failure.Id, moved!.ColumnId);
        Assert.Equal(ColumnExecutionStatus.Completed, history.Status);
        Assert.Equal("action_failure", history.Outcome);
        Assert.Null(await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow));
    }

    [Fact]
    public async Task Repeated_column_transition_is_stopped_before_another_agent_dispatch()
    {
        var project = await _projects.CreateProjectAsync("Routing loop protection");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var drafting = await _columns.CreateColumnAsync(project.Slug, "Drafting", pipelineId: pipeline.Id);
        var review = await _columns.CreateColumnAsync(project.Slug, "Review", pipelineId: pipeline.Id);
        var failure = await _columns.CreateColumnAsync(project.Slug, "Technical failure",
            pipelineId: pipeline.Id, role: ColumnRole.Failure);
        var writer = await SaveProcessor(project.Slug, drafting.Id, review.Id,
            technicalFailureColumnId: failure.Id);
        var reviewer = await SaveProcessor(project.Slug, review.Id, drafting.Id,
            technicalFailureColumnId: failure.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Needs iterations",
            status: drafting.Name, pipelineId: pipeline.Id, columnId: drafting.Id);
        var now = DateTime.UtcNow;

        var firstDraft = await _executions.ClaimNextAsync(project.Slug, writer, now);
        await _executions.CompleteAsync(project.Slug, firstDraft!, writer,
            new ColumnAgentResult("needs_review", []), writer.Name);
        var firstReview = await _executions.ClaimNextAsync(project.Slug, reviewer, now.AddMinutes(1));
        await _executions.CompleteAsync(project.Slug, firstReview!, reviewer,
            new ColumnAgentResult("changes_requested", []), reviewer.Name);
        var secondDraft = await _executions.ClaimNextAsync(project.Slug, writer, now.AddMinutes(2));
        await _executions.CompleteAsync(project.Slug, secondDraft!, writer,
            new ColumnAgentResult("needs_review", []), writer.Name);
        // Rows created before TargetColumnId was introduced must still protect an already
        // active loop immediately after an application upgrade.
        await using (var db = _projects.GetProjectDb(project.Slug))
        {
            var historicalRows = await db.ColumnExecutions
                .Where(execution => execution.ProcessorId == writer.Id).ToListAsync();
            foreach (var historical in historicalRows) historical.TargetColumnId = null;
            await db.SaveChangesAsync();
        }

        var blockedReview = await _executions.ClaimNextAsync(project.Slug, reviewer, now.AddMinutes(3));
        var moved = await _tickets.GetTicketAsync(project.Slug, ticket.Id);
        var history = await _executions.ListAsync(project.Slug, ticket.Id);

        Assert.Null(blockedReview);
        Assert.Equal(review.Id, moved!.ColumnId);
        var protection = Assert.Single(history, execution => execution.Outcome == "routing_loop");
        Assert.Equal(ColumnExecutionStatus.Failed, protection.Status);
        Assert.Null(protection.TargetColumnId);
        Assert.Equal(ColumnExecutionService.RoutingLoopError, protection.Error);
        Assert.Contains("same_transition_and_progress_fingerprint", protection.LoopDiagnosticJson);
        Assert.Contains(firstDraft!.Id, protection.LoopDiagnosticJson);
        Assert.Contains(secondDraft!.Id, protection.LoopDiagnosticJson);
    }

    [Fact]
    public async Task Ticket_217_regression_allows_repeated_transition_when_each_delivery_progresses()
    {
        var project = await _projects.CreateProjectAsync("Ticket 217 regression");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Delivery");
        var correction = await _columns.CreateColumnAsync(project.Slug, "À corriger", pipelineId: pipeline.Id);
        var validation = await _columns.CreateColumnAsync(project.Slug, "Validation", pipelineId: pipeline.Id);
        var failure = await _columns.CreateColumnAsync(project.Slug, "Abandonné",
            pipelineId: pipeline.Id, role: ColumnRole.Failure);
        var implementer = await SaveProcessor(project.Slug, correction.Id, validation.Id,
            technicalFailureColumnId: failure.Id);
        var validator = await SaveProcessor(project.Slug, validation.Id, correction.Id,
            technicalFailureColumnId: failure.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Documentation follow-up",
            status: correction.Name, pipelineId: pipeline.Id, columnId: correction.Id);
        var now = DateTime.UtcNow;

        var firstFix = await _executions.ClaimNextAsync(project.Slug, implementer, now);
        await _tickets.AddCommentAsync(project.Slug, ticket.Id, "Preuve: commit a1b2c3d", "programmer");
        await _executions.CompleteAsync(project.Slug, firstFix!, implementer,
            new ColumnAgentResult("completed", [], "Correction API livrée"), implementer.Name);
        var firstValidation = await _executions.ClaimNextAsync(project.Slug, validator, now.AddMinutes(1));
        await _executions.CompleteAsync(project.Slug, firstValidation!, validator,
            new ColumnAgentResult("changes_requested", [], "Documentation de reprise absente"), validator.Name);
        var secondFix = await _executions.ClaimNextAsync(project.Slug, implementer, now.AddMinutes(2));
        await _tickets.AddCommentAsync(project.Slug, ticket.Id, "Preuve: commit c95499f", "programmer");
        await _executions.CompleteAsync(project.Slug, secondFix!, implementer,
            new ColumnAgentResult("completed", [], "Documentation de reprise ajoutée"), implementer.Name);

        var nextValidation = await _executions.ClaimNextAsync(project.Slug, validator, now.AddMinutes(3));

        Assert.NotNull(nextValidation);
        var history = await _executions.ListAsync(project.Slug, ticket.Id);
        Assert.DoesNotContain(history, execution => execution.Outcome == "routing_loop");
        var persistedFixes = history.Where(execution => execution.ProcessorId == implementer.Id)
            .OrderBy(execution => execution.ClaimedAt).ToList();
        Assert.NotEqual(persistedFixes[0].ProgressFingerprint, persistedFixes[1].ProgressFingerprint);
    }

    [Fact]
    public async Task Automatic_comments_and_cosmetic_text_changes_do_not_bypass_the_loop_guard()
    {
        var project = await _projects.CreateProjectAsync("Loop noise filtering");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var drafting = await _columns.CreateColumnAsync(project.Slug, "Drafting", pipelineId: pipeline.Id);
        var review = await _columns.CreateColumnAsync(project.Slug, "Review", pipelineId: pipeline.Id);
        var writer = await SaveProcessor(project.Slug, drafting.Id, review.Id);
        var reviewer = await SaveProcessor(project.Slug, review.Id, drafting.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Noise", status: drafting.Name,
            pipelineId: pipeline.Id, columnId: drafting.Id);
        var now = DateTime.UtcNow;

        var first = await _executions.ClaimNextAsync(project.Slug, writer, now);
        await _tickets.AddCommentAsync(project.Slug, ticket.Id, "Automated heartbeat 1", "automation");
        await _executions.CompleteAsync(project.Slug, first!, writer,
            new ColumnAgentResult("completed", [], "Delivery complete!"), writer.Name);
        var validation = await _executions.ClaimNextAsync(project.Slug, reviewer, now.AddMinutes(1));
        await _executions.CompleteAsync(project.Slug, validation!, reviewer,
            new ColumnAgentResult("changes_requested", [], "Retry"), reviewer.Name);
        var second = await _executions.ClaimNextAsync(project.Slug, writer, now.AddMinutes(2));
        await _tickets.AddCommentAsync(project.Slug, ticket.Id, "Automated heartbeat 2", "automation");
        await _executions.CompleteAsync(project.Slug, second!, writer,
            new ColumnAgentResult("completed", [], "DELIVERY, complete."), writer.Name);

        Assert.Null(await _executions.ClaimNextAsync(project.Slug, reviewer, now.AddMinutes(3)));
        var history = await _executions.ListAsync(project.Slug, ticket.Id);
        var drafts = history.Where(execution => execution.ProcessorId == writer.Id)
            .OrderBy(execution => execution.ClaimedAt).ToList();
        Assert.Equal(drafts[0].ProgressFingerprint, drafts[1].ProgressFingerprint);
        Assert.Single(history, execution => execution.Outcome == "routing_loop");
        Assert.DoesNotContain("heartbeat", drafts[1].ProgressSignalsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task New_validation_diagnostic_or_human_evidence_is_persisted_as_progress()
    {
        var project = await _projects.CreateProjectAsync("Diagnostic and evidence progress");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var correction = await _columns.CreateColumnAsync(project.Slug, "Correction", pipelineId: pipeline.Id);
        var validation = await _columns.CreateColumnAsync(project.Slug, "Validation", pipelineId: pipeline.Id);
        var implementer = await SaveProcessor(project.Slug, correction.Id, validation.Id);
        var validator = await SaveProcessor(project.Slug, validation.Id, correction.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Evidence", status: correction.Name,
            pipelineId: pipeline.Id, columnId: correction.Id);
        var now = DateTime.UtcNow;

        var fix1 = await _executions.ClaimNextAsync(project.Slug, implementer, now);
        await _tickets.AddCommentAsync(project.Slug, ticket.Id, "Proof: API regression passes", "programmer");
        await _executions.CompleteAsync(project.Slug, fix1!, implementer,
            new ColumnAgentResult("completed", [], "Ready"), implementer.Name);
        var qa1 = await _executions.ClaimNextAsync(project.Slug, validator, now.AddMinutes(1));
        await _executions.CompleteAsync(project.Slug, qa1!, validator,
            new ColumnAgentResult("changes_requested", [], "Missing restart assertion"), validator.Name);
        var fix2 = await _executions.ClaimNextAsync(project.Slug, implementer, now.AddMinutes(2));
        await _tickets.AddCommentAsync(project.Slug, ticket.Id, "Proof: restart regression passes", "programmer");
        await _executions.CompleteAsync(project.Slug, fix2!, implementer,
            new ColumnAgentResult("completed", [], "Ready"), implementer.Name);
        var qa2 = await _executions.ClaimNextAsync(project.Slug, validator, now.AddMinutes(3));
        await _executions.CompleteAsync(project.Slug, qa2!, validator,
            new ColumnAgentResult("changes_requested", [], "Missing replay assertion"), validator.Name);

        Assert.NotNull(await _executions.ClaimNextAsync(project.Slug, implementer, now.AddMinutes(4)));
        var history = await _executions.ListAsync(project.Slug, ticket.Id);
        var fixes = history.Where(e => e.ProcessorId == implementer.Id).OrderBy(e => e.ClaimedAt).ToList();
        var validations = history.Where(e => e.ProcessorId == validator.Id).OrderBy(e => e.ClaimedAt).ToList();
        Assert.NotEqual(fixes[0].ProgressFingerprint, fixes[1].ProgressFingerprint);
        Assert.NotEqual(validations[0].ProgressFingerprint, validations[1].ProgressFingerprint);
    }

    [Fact]
    public async Task Delivery_checkpoint_survives_restart_and_replay_is_idempotent()
    {
        var project = await _projects.CreateProjectAsync("Durable delivery checkpoint");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Source", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Target", pipelineId: pipeline.Id);
        var back = await SaveProcessor(project.Slug, target.Id, source.Id);
        var forward = await SaveProcessor(project.Slug, source.Id, target.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Checkpoint", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var now = DateTime.UtcNow;

        var first = await _executions.ClaimNextAsync(project.Slug, forward, now);
        await _executions.CompleteActionAsync(project.Slug, first!, "publish-a");
        await _executions.CompleteActionAsync(project.Slug, first!, "publish-a");
        await _executions.CompleteAsync(project.Slug, first!, forward,
            new ColumnAgentResult("completed", [], "Published"), forward.Name);
        var return1 = await _executions.ClaimNextAsync(project.Slug, back, now.AddMinutes(1));
        await _executions.CompleteAsync(project.Slug, return1!, back,
            new ColumnAgentResult("completed", [], "Return"), back.Name);
        var second = await _executions.ClaimNextAsync(project.Slug, forward, now.AddMinutes(2));
        await _executions.CompleteActionAsync(project.Slug, second!, "publish-b");
        await _executions.CompleteAsync(project.Slug, second!, forward,
            new ColumnAgentResult("completed", [], "Published"), forward.Name);

        var restarted = new ColumnExecutionService(_projects, _tickets);
        Assert.NotNull(await restarted.ClaimNextAsync(project.Slug, back, now.AddMinutes(3)));
        var persisted = await restarted.ListAsync(project.Slug, ticket.Id);
        var forwards = persisted.Where(e => e.ProcessorId == forward.Id).OrderBy(e => e.ClaimedAt).ToList();
        Assert.Equal(["publish-a"], forwards[0].CompletedActionIds);
        Assert.NotEqual(forwards[0].ProgressFingerprint, forwards[1].ProgressFingerprint);
        Assert.Equal("completed", forwards[1].AgentResult!.Outcome);
    }

    [Fact]
    public async Task Retrying_a_synthetic_loop_execution_resumes_without_losing_business_result()
    {
        var project = await _projects.CreateProjectAsync("Loop recovery");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Source", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Target", pipelineId: pipeline.Id);
        var forward = await SaveProcessor(project.Slug, source.Id, target.Id);
        var back = await SaveProcessor(project.Slug, target.Id, source.Id);
        var ticket = await _tickets.CreateTicketAsync(project.Slug, "Recover", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var now = DateTime.UtcNow;

        var first = await _executions.ClaimNextAsync(project.Slug, forward, now);
        await _executions.CompleteAsync(project.Slug, first!, forward,
            new ColumnAgentResult("completed", [], "Stable result"), forward.Name);
        var return1 = await _executions.ClaimNextAsync(project.Slug, back, now.AddMinutes(1));
        await _executions.CompleteAsync(project.Slug, return1!, back,
            new ColumnAgentResult("completed", [], "Return"), back.Name);
        var second = await _executions.ClaimNextAsync(project.Slug, forward, now.AddMinutes(2));
        await _executions.CompleteAsync(project.Slug, second!, forward,
            new ColumnAgentResult("completed", [], "Stable result"), forward.Name);
        Assert.Null(await _executions.ClaimNextAsync(project.Slug, back, now.AddMinutes(3)));
        var protection = Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id),
            execution => execution.Outcome == "routing_loop");

        Assert.True(await _executions.RetryAsync(project.Slug, protection.Id));
        var resumed = await _executions.ClaimNextAsync(project.Slug, back, now.AddMinutes(4));
        var persistedFirst = (await _executions.ListAsync(project.Slug, ticket.Id)).Single(e => e.Id == first!.Id);
        Assert.Equal(protection.Id, resumed!.Id);
        Assert.Equal("Stable result", persistedFirst.Summary);
        Assert.Equal("completed", persistedFirst.AgentResult!.Outcome);
    }

    [Fact]
    public async Task Progress_schema_is_added_to_historical_execution_table()
    {
        var project = await _projects.CreateProjectAsync("Legacy progress schema");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Main");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id);
        var processor = await SaveProcessor(project.Slug, source.Id, target.Id);
        await _tickets.CreateTicketAsync(project.Slug, "Legacy row", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        await using (var db = _projects.GetProjectDb(project.Slug))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS ColumnExecutions");
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE ColumnExecutions (
                    Id TEXT NOT NULL PRIMARY KEY, ProcessorId INTEGER NOT NULL, TicketId INTEGER NOT NULL,
                    Status INTEGER NOT NULL, Attempt INTEGER NOT NULL DEFAULT 1, ClaimedAt TEXT NOT NULL,
                    AvailableAt TEXT NULL, EndedAt TEXT NULL, RunId TEXT NULL, Outcome TEXT NULL,
                    Summary TEXT NULL, Error TEXT NULL, TargetColumnId INTEGER NULL,
                    CompletedActionIdsJson TEXT NOT NULL DEFAULT '[]', CurrentActionId TEXT NULL,
                    AgentCompleted INTEGER NOT NULL DEFAULT 0, AgentResultJson TEXT NULL);
                """);
        }

        Assert.NotNull(await _executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow));
        await using var migrated = _projects.GetProjectDb(project.Slug);
        var columns = await migrated.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_table_info('ColumnExecutions')").ToListAsync();
        Assert.Contains("ProgressFingerprint", columns);
        Assert.Contains("ProgressSignalsJson", columns);
        Assert.Contains("LoopDiagnosticJson", columns);
    }

    private Task<ColumnProcessor> SaveProcessor(
        string slug, int sourceId, int defaultTargetId,
        TicketSelectionOrder selectionOrder = TicketSelectionOrder.Position,
        int maxAttempts = 3, int? technicalFailureColumnId = null) =>
        _processors.SaveAsync(slug, sourceId, "Worker", "Process ticket.", null,
            true, 20, [], [], [], selectionOrder, maxAttempts, 1,
            defaultTargetId, technicalFailureColumnId, []);

    public void Dispose() => _temp.Dispose();
}
