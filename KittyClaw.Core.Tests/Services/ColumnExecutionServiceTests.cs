using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

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
    public async Task Completion_routes_by_outcome_across_pipelines_using_stable_column_ids()
    {
        var project = await _projects.CreateProjectAsync("Routing");
        var intake = await _pipelines.CreateAsync(project.Slug, "Intake");
        var delivery = await _pipelines.CreateAsync(project.Slug, "Delivery");
        var source = await _columns.CreateColumnAsync(project.Slug, "Assess", pipelineId: intake.Id);
        var accepted = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: delivery.Id);
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
        Assert.Equal(ColumnExecutionStatus.Completed,
            Assert.Single(await _executions.ListAsync(project.Slug, ticket.Id)).Status);
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

    private Task<ColumnProcessor> SaveProcessor(
        string slug, int sourceId, int defaultTargetId,
        TicketSelectionOrder selectionOrder = TicketSelectionOrder.Position,
        int maxAttempts = 3, int? technicalFailureColumnId = null) =>
        _processors.SaveAsync(slug, sourceId, "Worker", "Process ticket.", null,
            true, 20, [], [], [], selectionOrder, maxAttempts, 1,
            defaultTargetId, technicalFailureColumnId, []);

    public void Dispose() => _temp.Dispose();
}
