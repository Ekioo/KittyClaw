using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class PipelineServiceTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ProjectService _projects;
    private readonly PipelineService _pipelines;
    private readonly ColumnService _columns;

    public PipelineServiceTests()
    {
        _projects = new ProjectService(_temp.Path);
        _pipelines = new PipelineService(_projects);
        _columns = new ColumnService(_projects);
    }

    [Fact]
    public void Persisted_column_role_values_remain_backward_compatible()
    {
        Assert.Equal(0, (int)ColumnRole.Normal);
        Assert.Equal(1, (int)ColumnRole.Waiting);
        Assert.Equal(2, (int)ColumnRole.Success);
        Assert.Equal(3, (int)ColumnRole.Failure);
        Assert.Equal(4, (int)ColumnRole.OwnerAction);
        Assert.Equal(5, (int)ColumnRole.Blocked);
    }

    [Fact]
    public async Task Existing_board_is_migrated_to_a_default_pipeline()
    {
        var project = await _projects.CreateProjectAsync("Legacy");

        var pipelines = await _pipelines.ListAsync(project.Slug);
        var columns = await _columns.ListColumnsAsync(project.Slug);

        var pipeline = Assert.Single(pipelines);
        Assert.True(pipeline.IsDefault);
        Assert.Equal(PipelineService.DefaultPipelineSlug, pipeline.Slug);
        Assert.All(columns, column => Assert.Equal(pipeline.Id, column.PipelineId));
        Assert.Equal(ColumnRole.Blocked, columns.Single(c => c.Name == "Blocked").Role);
        Assert.Equal(ColumnRole.Success, columns.Single(c => c.Name == "Done").Role);
    }

    [Fact]
    public async Task Pipelines_can_reuse_column_names()
    {
        var project = await _projects.CreateProjectAsync("Multiple workflows");
        var editorial = await _pipelines.CreateAsync(project.Slug, "Editorial");
        var development = await _pipelines.CreateAsync(project.Slug, "Development");

        var editorialReview = await _columns.CreateColumnAsync(project.Slug, "Review", pipelineId: editorial.Id);
        var developmentReview = await _columns.CreateColumnAsync(project.Slug, "Review", pipelineId: development.Id);

        Assert.NotEqual(editorialReview.Id, developmentReview.Id);
        Assert.Equal(editorial.Id, editorialReview.PipelineId);
        Assert.Equal(development.Id, developmentReview.PipelineId);
    }

    [Fact]
    public async Task Column_can_be_inserted_at_an_exact_board_position()
    {
        var project = await _projects.CreateProjectAsync("Contextual insertion");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial");
        await _columns.CreateColumnAsync(project.Slug, "Draft", pipelineId: pipeline.Id);
        await _columns.CreateColumnAsync(project.Slug, "Review", pipelineId: pipeline.Id);

        var inserted = await _columns.CreateColumnAsync(
            project.Slug, "Fact check", pipelineId: pipeline.Id, insertIndex: 1);
        var columns = await _columns.ListColumnsAsync(project.Slug, pipeline.Id);

        Assert.Equal(["Draft", "Fact check", "Review"], columns.Select(column => column.Name));
        Assert.Equal(1, inserted.SortOrder);
    }

    [Fact]
    public async Task Waiting_column_preserves_configurable_user_guidance()
    {
        var project = await _projects.CreateProjectAsync("Guided handoff");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial");
        var column = await _columns.CreateColumnAsync(project.Slug, "Approval",
            pipelineId: pipeline.Id, role: ColumnRole.OwnerAction,
            userGuidance: "Move to **Published** to approve.");

        Assert.Equal("Move to **Published** to approve.", column.UserGuidance);

        await _columns.UpdateColumnAsync(project.Slug, column.Id,
            userGuidance: "Add a comment, then move to **Published**.");
        var reloaded = Assert.Single(await _columns.ListColumnsAsync(project.Slug, pipeline.Id));

        Assert.Equal("Add a comment, then move to **Published**.", reloaded.UserGuidance);
    }

    [Fact]
    public async Task Renaming_pipeline_preserves_stable_identity_and_columns()
    {
        var project = await _projects.CreateProjectAsync("Rename workflow");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial");
        var column = await _columns.CreateColumnAsync(project.Slug, "Draft", pipelineId: pipeline.Id);

        var renamed = await _pipelines.UpdateAsync(project.Slug, pipeline.Id, "Content production");
        var columns = await _columns.ListColumnsAsync(project.Slug, pipeline.Id);

        Assert.NotNull(renamed);
        Assert.Equal(pipeline.Id, renamed.Id);
        Assert.Equal(pipeline.Slug, renamed.Slug);
        Assert.Equal("Content production", renamed.Name);
        Assert.Equal(column.Id, Assert.Single(columns).Id);
        Assert.Equal(pipeline.Id, columns[0].PipelineId);
    }

    [Fact]
    public async Task Renaming_pipeline_preserves_ticket_links()
    {
        var project = await _projects.CreateProjectAsync("Ticket workflow identity");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial");
        var column = await _columns.CreateColumnAsync(project.Slug, "Draft", pipelineId: pipeline.Id);
        var tickets = new TicketService(_projects, new MemberService(_projects));
        var ticket = await tickets.CreateTicketAsync(
            project.Slug, "Article", status: column.Name,
            pipelineId: pipeline.Id, columnId: column.Id);

        await _pipelines.UpdateAsync(project.Slug, pipeline.Id, "Content production");
        var reloaded = await tickets.GetTicketAsync(project.Slug, ticket.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(pipeline.Id, reloaded.PipelineId);
        Assert.Equal(column.Id, reloaded.ColumnId);
        Assert.Equal("Draft", reloaded.Status);
    }

    [Fact]
    public async Task Ticket_can_move_across_pipelines_by_stable_column_id()
    {
        var project = await _projects.CreateProjectAsync("Cross pipeline move");
        var intake = await _pipelines.CreateAsync(project.Slug, "Intake");
        var delivery = await _pipelines.CreateAsync(project.Slug, "Delivery");
        var source = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: intake.Id);
        var target = await _columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: delivery.Id);
        var tickets = new TicketService(_projects, new MemberService(_projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Work", status: source.Name,
            pipelineId: intake.Id, columnId: source.Id);

        var moved = await tickets.UpdateTicketAsync(project.Slug, ticket.Id, columnId: target.Id);

        Assert.Equal(delivery.Id, moved!.PipelineId);
        Assert.Equal(target.Id, moved.ColumnId);
        Assert.Equal("Ready", moved.Status);
    }

    [Fact]
    public async Task Subticket_summary_exposes_destination_column_role()
    {
        var project = await _projects.CreateProjectAsync("Child outcome");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Validation");
        var work = await _columns.CreateColumnAsync(project.Slug, "Work", pipelineId: pipeline.Id);
        var success = await _columns.CreateColumnAsync(project.Slug, "Accepted", pipelineId: pipeline.Id, role: ColumnRole.Success);
        var tickets = new TicketService(_projects, new MemberService(_projects));
        var parent = await tickets.CreateTicketAsync(project.Slug, "Parent", status: work.Name,
            pipelineId: pipeline.Id, columnId: work.Id);
        await tickets.CreateTicketAsync(project.Slug, "Child", status: success.Name, parentId: parent.Id,
            pipelineId: pipeline.Id, columnId: success.Id);

        var loaded = await tickets.GetTicketAsync(project.Slug, parent.Id);

        Assert.Equal(ColumnRole.Success, Assert.Single(loaded!.SubTickets).ColumnRole);
    }

    public void Dispose() => _temp.Dispose();
}
