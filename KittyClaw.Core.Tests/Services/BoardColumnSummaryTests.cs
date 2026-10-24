using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class BoardColumnSummaryTests
{
    [Fact]
    public async Task EmptyProject_ShowsAllColumnsInBoardOrderWithZeroCounts()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Empty summary");
        var columns = new ColumnService(projects);

        var board = await columns.ListColumnsAsync(project.Slug);
        var summary = await columns.ListColumnSummariesAsync(project.Slug);

        Assert.Equal(board.Select(c => c.Id), summary.Select(c => c.ColumnId));
        Assert.All(summary, c => Assert.Equal(0, c.TicketCount));
    }

    [Fact]
    public async Task CountsMatchRootKanbanAndReflectMovesWithoutCountingChildrenTwice()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Root summary");
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var parent = await tickets.CreateTicketAsync(project.Slug, "Parent", status: "Todo");
        await tickets.CreateTicketAsync(project.Slug, "Child", status: "Todo", parentId: parent.Id);
        await tickets.CreateTicketAsync(project.Slug, "Completed", status: "Done");

        var before = await columns.ListColumnSummariesAsync(project.Slug);
        Assert.Equal(1, before.Single(c => c.Name == "Todo").TicketCount);
        Assert.Equal(2, before.Sum(c => c.TicketCount));

        await tickets.UpdateTicketAsync(project.Slug, parent.Id, status: "Done");
        var after = await columns.ListColumnSummariesAsync(project.Slug);
        Assert.Equal(0, after.Single(c => c.Name == "Todo").TicketCount);
        Assert.Equal(2, after.Single(c => c.Name == "Done").TicketCount);
    }

    [Fact]
    public async Task IdenticallyNamedColumnsRemainSeparateAcrossPipelines()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Pipeline summary");
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var mainTodo = (await columns.ListColumnsAsync(project.Slug)).Single(c => c.Name == "Todo");
        var release = await new PipelineService(projects).CreateAsync(project.Slug, "Release");
        var releaseTodo = await columns.CreateColumnAsync(project.Slug, "Todo", pipelineId: release.Id);
        await tickets.CreateTicketAsync(project.Slug, "Main work", status: "Todo");
        await tickets.CreateTicketAsync(project.Slug, "Release work", status: "Todo", pipelineId: release.Id, columnId: releaseTodo.Id);
        await tickets.CreateTicketAsync(project.Slug, "More release work", status: "Todo", pipelineId: release.Id, columnId: releaseTodo.Id);

        var summary = await columns.ListColumnSummariesAsync(project.Slug);
        Assert.Equal(1, summary.Single(c => c.ColumnId == mainTodo.Id).TicketCount);
        var releaseSummary = summary.Single(c => c.ColumnId == releaseTodo.Id);
        Assert.Equal("Release", releaseSummary.PipelineName);
        Assert.Equal(2, releaseSummary.TicketCount);
    }
}
