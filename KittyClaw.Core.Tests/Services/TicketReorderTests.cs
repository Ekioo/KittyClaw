using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketReorderTests
{
    private static (TicketService tickets, string slug) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("reorder-test").GetAwaiter().GetResult();
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        return (tickets, project.Slug);
    }

    [Fact]
    public async Task ReorderTicketAsync_RaisesTicketStatusChanged_WhenColumnChanges()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);

        var ticket = await svc.CreateTicketAsync(slug, "T1", status: "Todo");

        string? capturedOld = null, capturedNew = null;
        svc.TicketStatusChanged += (_, _, oldStatus, newStatus) =>
        {
            capturedOld = oldStatus;
            capturedNew = newStatus;
        };

        await svc.ReorderTicketAsync(slug, ticket.Id, "InProgress", 0);

        Assert.Equal("Todo", capturedOld);
        Assert.Equal("InProgress", capturedNew);
    }

    [Fact]
    public async Task ReorderTicketAsync_DoesNotRaiseTicketStatusChanged_WhenSameColumn()
    {
        using var tmp = new TempDir();
        var (svc, slug) = BuildSut(tmp);

        var ticket = await svc.CreateTicketAsync(slug, "T1", status: "Todo");

        var fired = false;
        svc.TicketStatusChanged += (_, _, _, _) => fired = true;

        await svc.ReorderTicketAsync(slug, ticket.Id, "Todo", 0);

        Assert.False(fired);
    }
}
