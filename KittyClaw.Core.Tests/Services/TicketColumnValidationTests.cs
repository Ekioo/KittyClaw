using KittyClaw.Core.Services;
using KittyClaw.Core.Models;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketColumnValidationTests
{
    private static (TicketService tickets, string slug) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("col-val").GetAwaiter().GetResult();
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        // Ensure default columns are seeded (same path as the board).
        var columns = new ColumnService(projects);
        columns.ListColumnsAsync(project.Slug).GetAwaiter().GetResult();
        return (tickets, project.Slug);
    }

    [Fact]
    public async Task CreateTicket_UnknownStatus_ThrowsAndDoesNotPersist()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateTicketAsync(slug, "Ghost", status: "DoesNotExist"));
        Assert.Contains("DoesNotExist", ex.Message);

        var list = await svc.ListTicketsAsync(slug);
        Assert.DoesNotContain(list, t => t.Title == "Ghost");
    }

    [Fact]
    public async Task CreateTicket_ValidStatus_CanonicalizesCase()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);

        var t = await svc.CreateTicketAsync(slug, "Cased", status: "todo");
        Assert.Equal("Todo", t.Status);
    }

    [Fact]
    public async Task MoveTicket_UnknownStatus_Throws()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);
        var t = await svc.CreateTicketAsync(slug, "T1", status: "Todo");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MoveTicketAsync(slug, t.Id, "NoSuchColumn"));
        Assert.Contains("NoSuchColumn", ex.Message);

        var reloaded = await svc.GetTicketAsync(slug, t.Id);
        Assert.Equal("Todo", reloaded!.Status);
    }

    [Fact]
    public async Task UpdateTicket_WithComment_CommitsDecisionAndMoveTogether()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "Decision", status: "Todo");

        await svc.UpdateTicketAsync(slug, ticket.Id, status: "Done", comment: "Decision recorded.");

        var reloaded = await svc.GetTicketAsync(slug, ticket.Id);
        Assert.Equal("Done", reloaded!.Status);
        Assert.Contains(reloaded.Comments, comment => comment.Content == "Decision recorded.");
    }

    [Fact]
    public async Task UpdateTicket_WithComment_InvalidDestinationPersistsNeitherChange()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "Decision", status: "Todo");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateTicketAsync(slug, ticket.Id, status: "Missing", comment: "Must not persist."));

        var reloaded = await svc.GetTicketAsync(slug, ticket.Id);
        Assert.Equal("Todo", reloaded!.Status);
        Assert.DoesNotContain(reloaded.Comments, comment => comment.Content == "Must not persist.");
    }

    [Fact]
    public async Task ReorderTicket_UnknownStatus_Throws()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);
        var t = await svc.CreateTicketAsync(slug, "T1", status: "Todo");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReorderTicketAsync(slug, t.Id, "Phantom", 0));

        var reloaded = await svc.GetTicketAsync(slug, t.Id);
        Assert.Equal("Todo", reloaded!.Status);
    }

    [Fact]
    public async Task OwnerActionRole_AssignsOwnerOnEntry_AndClearsTheAutomaticAssignmentOnExit()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("owner-action");
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var pipeline = await pipelines.CreateAsync(project.Slug, "Review");
        var ready = await columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var decision = await columns.CreateColumnAsync(project.Slug, "Decision", pipelineId: pipeline.Id,
            role: ColumnRole.OwnerAction);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Choose", status: ready.Name,
            pipelineId: pipeline.Id, columnId: ready.Id);

        var waiting = await tickets.MoveTicketAsync(project.Slug, ticket.Id, decision.Name);
        Assert.Equal("owner", waiting!.AssignedTo);

        var resumed = await tickets.MoveTicketAsync(project.Slug, ticket.Id, ready.Name);
        Assert.Null(resumed!.AssignedTo);
    }

    [Fact]
    public async Task OwnerActionRole_AppliesToTicketsCreatedDirectlyInTheColumn()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("owner-action-create");
        var pipeline = await new PipelineService(projects).CreateAsync(project.Slug, "Review");
        var column = await new ColumnService(projects).CreateColumnAsync(project.Slug, "Decision",
            pipelineId: pipeline.Id, role: ColumnRole.OwnerAction);
        var tickets = new TicketService(projects, new MemberService(projects));

        var ticket = await tickets.CreateTicketAsync(project.Slug, "Choose", status: column.Name,
            pipelineId: pipeline.Id, columnId: column.Id);

        Assert.Equal("owner", ticket.AssignedTo);
    }

    [Fact]
    public async Task CountOwnerActionTicketsAsync_UsesColumnRoleAndTracksMoves()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("owner-action-count");
        var pipeline = await new PipelineService(projects).CreateAsync(project.Slug, "Review");
        var columns = new ColumnService(projects);
        var ready = await columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var decision = await columns.CreateColumnAsync(project.Slug, "Custom renamed handoff",
            pipelineId: pipeline.Id, role: ColumnRole.OwnerAction);
        var tickets = new TicketService(projects, new MemberService(projects));
        var first = await tickets.CreateTicketAsync(project.Slug, "Choose A", status: decision.Name,
            pipelineId: pipeline.Id, columnId: decision.Id);
        await tickets.CreateTicketAsync(project.Slug, "Choose B", status: decision.Name,
            pipelineId: pipeline.Id, columnId: decision.Id);
        await tickets.CreateTicketAsync(project.Slug, "Ordinary work", status: ready.Name,
            pipelineId: pipeline.Id, columnId: ready.Id);

        Assert.Equal(2, await tickets.CountOwnerActionTicketsAsync(project.Slug));

        await tickets.MoveTicketAsync(project.Slug, first.Id, ready.Name);

        Assert.Equal(1, await tickets.CountOwnerActionTicketsAsync(project.Slug));
    }
}
