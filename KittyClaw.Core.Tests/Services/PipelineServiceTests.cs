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
    public async Task Existing_board_is_migrated_to_a_default_pipeline()
    {
        var project = await _projects.CreateProjectAsync("Legacy");

        var pipelines = await _pipelines.ListAsync(project.Slug);
        var columns = await _columns.ListColumnsAsync(project.Slug);

        var pipeline = Assert.Single(pipelines);
        Assert.True(pipeline.IsDefault);
        Assert.Equal(PipelineService.DefaultPipelineSlug, pipeline.Slug);
        Assert.All(columns, column => Assert.Equal(pipeline.Id, column.PipelineId));
        Assert.Equal(ColumnRole.Waiting, columns.Single(c => c.Name == "Blocked").Role);
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

    public void Dispose() => _temp.Dispose();
}
