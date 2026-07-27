using KittyClaw.Core.Services;
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
}
