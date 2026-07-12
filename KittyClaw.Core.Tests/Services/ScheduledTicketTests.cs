using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Tests for the "Scheduled" status (feature #99): scheduling a ticket for a future FireAt and
/// auto-promoting it to its target column once due.
/// </summary>
public sealed class ScheduledTicketTests
{
    private static (ProjectService projects, TicketService tickets, string slug) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("scheduled-test").GetAwaiter().GetResult();
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        return (projects, tickets, project.Slug);
    }

    [Fact]
    public async Task ScheduleTicketAsync_SetsStatusFireAtAndTarget_AndRaisesStatusChanged()
    {
        using var tmp = new TempDir();
        var (_, svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "Post X", status: "Todo");
        var fireAt = new DateTime(2026, 7, 10, 13, 0, 0, DateTimeKind.Utc);

        string? from = null, to = null;
        svc.TicketStatusChanged += (_, _, o, n) => { from = o; to = n; };

        var result = await svc.ScheduleTicketAsync(slug, ticket.Id, fireAt, "Todo", "lain");

        Assert.NotNull(result);
        Assert.Equal("Scheduled", result!.Status);
        Assert.Equal(fireAt, result.FireAt);
        Assert.Equal("Todo", result.ScheduleTarget);
        Assert.Equal("Todo", from);
        Assert.Equal("Scheduled", to);
    }

    [Fact]
    public async Task ScheduleTicketAsync_DefaultsTargetToTodo_WhenBlankOrUnknownGiven()
    {
        using var tmp = new TempDir();
        var (_, svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "Newsletter", status: "Backlog");
        var fireAt = DateTime.UtcNow.AddDays(3);

        var result = await svc.ScheduleTicketAsync(slug, ticket.Id, fireAt, "", "owner");

        Assert.Equal("Scheduled", result!.Status);
        Assert.Equal("Todo", result.ScheduleTarget);
    }

    [Fact]
    public async Task ListDueScheduledTicketIdsAsync_ReturnsOnlyDueTickets()
    {
        using var tmp = new TempDir();
        var (_, svc, slug) = BuildSut(tmp);
        var now = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

        var due = await svc.CreateTicketAsync(slug, "Due", status: "Todo");
        var future = await svc.CreateTicketAsync(slug, "Future", status: "Todo");
        var notScheduled = await svc.CreateTicketAsync(slug, "Plain", status: "Todo");

        await svc.ScheduleTicketAsync(slug, due.Id, now.AddMinutes(-1), "Todo", "owner");
        await svc.ScheduleTicketAsync(slug, future.Id, now.AddDays(2), "Todo", "owner");

        var dueIds = await svc.ListDueScheduledTicketIdsAsync(slug, now);

        Assert.Contains(due.Id, dueIds);
        Assert.DoesNotContain(future.Id, dueIds);
        Assert.DoesNotContain(notScheduled.Id, dueIds);
    }

    [Fact]
    public async Task PromoteScheduledAsync_MovesToTarget_ClearsFireAt_AndRaisesStatusChanged()
    {
        using var tmp = new TempDir();
        var (_, svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "SEO article", status: "Backlog");
        await svc.ScheduleTicketAsync(slug, ticket.Id, DateTime.UtcNow.AddDays(-1), "Review", "owner");

        string? from = null, to = null;
        svc.TicketStatusChanged += (_, _, o, n) => { from = o; to = n; };

        var promoted = await svc.PromoteScheduledAsync(slug, ticket.Id);

        Assert.NotNull(promoted);
        Assert.Equal("Review", promoted!.Status);
        Assert.Null(promoted.FireAt);
        Assert.Null(promoted.ScheduleTarget);
        Assert.Equal("Scheduled", from);
        Assert.Equal("Review", to);
    }

    [Fact]
    public async Task MoveTicketAsync_OutOfScheduled_ClearsFireAtAndTarget()
    {
        using var tmp = new TempDir();
        var (_, svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "Post X", status: "Todo");
        await svc.ScheduleTicketAsync(slug, ticket.Id, DateTime.UtcNow.AddDays(2), "Todo", "owner");

        var moved = await svc.MoveTicketAsync(slug, ticket.Id, "Backlog");

        Assert.NotNull(moved);
        Assert.Equal("Backlog", moved!.Status);
        Assert.Null(moved.FireAt);
        Assert.Null(moved.ScheduleTarget);
    }

    [Fact]
    public async Task PromoteScheduledAsync_IsNoOp_WhenTicketNotScheduled()
    {
        using var tmp = new TempDir();
        var (_, svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "Plain", status: "Todo");

        var result = await svc.PromoteScheduledAsync(slug, ticket.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task PromotionService_PromotesDue_LeavesFutureScheduled()
    {
        using var tmp = new TempDir();
        var (projects, svc, slug) = BuildSut(tmp);
        var now = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

        var due = await svc.CreateTicketAsync(slug, "Due", status: "Todo");
        var future = await svc.CreateTicketAsync(slug, "Future", status: "Todo");
        await svc.ScheduleTicketAsync(slug, due.Id, now.AddMinutes(-5), "Todo", "owner");
        await svc.ScheduleTicketAsync(slug, future.Id, now.AddDays(1), "Todo", "owner");

        var service = new ScheduledPromotionService(projects, svc, NullLogger<ScheduledPromotionService>.Instance);
        var promotedCount = await service.PromoteDueAsync(now);

        Assert.Equal(1, promotedCount);
        var dueTicket = await svc.GetTicketAsync(slug, due.Id);
        var futureTicket = await svc.GetTicketAsync(slug, future.Id);
        Assert.Equal("Todo", dueTicket!.Status);
        Assert.Null(dueTicket.FireAt);
        Assert.Equal("Scheduled", futureTicket!.Status);
        Assert.NotNull(futureTicket.FireAt);
    }
}
