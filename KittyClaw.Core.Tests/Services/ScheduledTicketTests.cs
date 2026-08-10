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
    public async Task UpdateTicketAsync_TransferFromScheduledToPlanifie_PreservesWake()
    {
        using var tmp = new TempDir();
        var (projects, svc, slug) = BuildSut(tmp);
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var targetPipeline = await pipelines.CreateAsync(slug, "Distribution");
        var planned = await columns.CreateColumnAsync(
            slug, "Planifié", pipelineId: targetPipeline.Id, role: KittyClaw.Core.Models.ColumnRole.Waiting);
        await columns.CreateColumnAsync(slug, "À traiter", pipelineId: targetPipeline.Id);
        var ticket = await svc.CreateTicketAsync(slug, "Post X", status: "Todo");
        var fireAt = DateTime.UtcNow.AddDays(2);
        await svc.ScheduleTicketAsync(slug, ticket.Id, fireAt, "Todo", "owner");

        var moved = await svc.UpdateTicketAsync(
            slug, ticket.Id, author: "lain", status: planned.Name,
            pipelineId: targetPipeline.Id, columnId: planned.Id);

        Assert.NotNull(moved);
        Assert.Equal("Planifié", moved!.Status);
        Assert.Equal(fireAt, moved.FireAt);
        Assert.Equal("Todo", moved.ScheduleTarget);
    }

    [Fact]
    public async Task ScheduleAndPromote_UsesLocalizedPipelineColumns()
    {
        using var tmp = new TempDir();
        var (projects, svc, slug) = BuildSut(tmp);
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var pipeline = await pipelines.CreateAsync(slug, "Distribution");
        var planned = await columns.CreateColumnAsync(
            slug, "Planifié", pipelineId: pipeline.Id, role: KittyClaw.Core.Models.ColumnRole.Waiting);
        var ready = await columns.CreateColumnAsync(slug, "À traiter", pipelineId: pipeline.Id);
        var ticket = await svc.CreateTicketAsync(
            slug, "Checkpoint", status: ready.Name, pipelineId: pipeline.Id, columnId: ready.Id);

        var scheduled = await svc.ScheduleTicketAsync(
            slug, ticket.Id, DateTime.UtcNow.AddMinutes(-1), ready.Name, "owner");
        var dueIds = await svc.ListDueScheduledTicketIdsAsync(slug, DateTime.UtcNow);
        var promoted = await svc.PromoteScheduledAsync(slug, ticket.Id);

        Assert.Equal(planned.Id, scheduled!.ColumnId);
        Assert.Contains(ticket.Id, dueIds);
        Assert.Equal(ready.Id, promoted!.ColumnId);
        Assert.Null(promoted.FireAt);
    }

    [Fact]
    public async Task ProcessorSchedule_InArbitrarilyNamedWaitingColumn_IsDueAndPromoted()
    {
        using var tmp = new TempDir();
        var (projects, svc, slug) = BuildSut(tmp);
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var pipeline = await pipelines.CreateAsync(slug, "Publication");
        var waiting = await columns.CreateColumnAsync(
            slug, "Publication différée", pipelineId: pipeline.Id,
            role: KittyClaw.Core.Models.ColumnRole.Waiting);
        var ready = await columns.CreateColumnAsync(slug, "Prêt à publier", pipelineId: pipeline.Id);
        var ticket = await svc.CreateTicketAsync(
            slug, "Annonce", status: ready.Name, pipelineId: pipeline.Id, columnId: ready.Id);
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = projects.GetProjectDb(slug))
        {
            var scheduled = await db.Tickets.FindAsync(ticket.Id);
            scheduled!.Status = waiting.Name;
            scheduled.ColumnId = waiting.Id;
            scheduled.FireAt = now.AddMinutes(-1);
            scheduled.ScheduleTarget = ready.Name;
            await db.SaveChangesAsync();
        }

        var dueIds = await svc.ListDueScheduledTicketIdsAsync(slug, now);
        var promoted = await svc.PromoteScheduledAsync(slug, ticket.Id);

        Assert.Contains(ticket.Id, dueIds);
        Assert.Equal(ready.Id, promoted!.ColumnId);
        Assert.Equal(ready.Name, promoted.Status);
        Assert.Null(promoted.FireAt);
        Assert.Null(promoted.ScheduleTarget);
    }

    [Fact]
    public async Task RenamingWakeTarget_UpdatesPendingSchedule_AndPromotionUsesNewName()
    {
        using var tmp = new TempDir();
        var (projects, svc, slug) = BuildSut(tmp);
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var pipeline = await pipelines.CreateAsync(slug, "Migration");
        var waiting = await columns.CreateColumnAsync(
            slug, "Observation", pipelineId: pipeline.Id,
            role: KittyClaw.Core.Models.ColumnRole.Waiting);
        var target = await columns.CreateColumnAsync(slug, "À lancer", pipelineId: pipeline.Id);
        var ticket = await svc.CreateTicketAsync(
            slug, "Reprise", status: target.Name, pipelineId: pipeline.Id, columnId: target.Id);
        var fireAt = DateTime.UtcNow.AddMinutes(-1);

        await using (var db = projects.GetProjectDb(slug))
        {
            var scheduled = await db.Tickets.FindAsync(ticket.Id);
            scheduled!.Status = waiting.Name;
            scheduled.ColumnId = waiting.Id;
            scheduled.FireAt = fireAt;
            scheduled.ScheduleTarget = target.Name;
            await db.SaveChangesAsync();
        }

        await columns.UpdateColumnAsync(slug, target.Id, name: "Prêt à exécuter");
        var pending = await svc.GetTicketAsync(slug, ticket.Id);
        var promoted = await svc.PromoteScheduledAsync(slug, ticket.Id);

        Assert.Equal("Prêt à exécuter", pending!.ScheduleTarget);
        Assert.Equal(target.Id, promoted!.ColumnId);
        Assert.Equal("Prêt à exécuter", promoted.Status);
    }

    [Fact]
    public async Task MissingWakeTarget_ProducesActionableDiagnostic_AndKeepsSchedulePending()
    {
        using var tmp = new TempDir();
        var (projects, svc, slug) = BuildSut(tmp);
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var pipeline = await pipelines.CreateAsync(slug, "Diagnostic");
        var waiting = await columns.CreateColumnAsync(
            slug, "Observation", pipelineId: pipeline.Id,
            role: KittyClaw.Core.Models.ColumnRole.Waiting);
        var ticket = await svc.CreateTicketAsync(
            slug, "Cible perdue", status: waiting.Name, pipelineId: pipeline.Id, columnId: waiting.Id);

        await using (var db = projects.GetProjectDb(slug))
        {
            var scheduled = await db.Tickets.FindAsync(ticket.Id);
            scheduled!.FireAt = DateTime.UtcNow.AddMinutes(-1);
            scheduled.ScheduleTarget = "Colonne supprimée";
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PromoteScheduledAsync(slug, ticket.Id));
        var pending = await svc.GetTicketAsync(slug, ticket.Id);

        Assert.Contains($"ticket #{ticket.Id}", error.Message);
        Assert.Contains("Colonne supprimée", error.Message);
        Assert.Contains($"pipeline #{pipeline.Id}", error.Message);
        Assert.NotNull(pending!.FireAt);
        Assert.Equal("Colonne supprimée", pending.ScheduleTarget);
    }

    [Fact]
    public async Task ConcurrentPromotion_ConsumesScheduleOnce_AndSignalsOnce()
    {
        using var tmp = new TempDir();
        var (_, svc, slug) = BuildSut(tmp);
        var ticket = await svc.CreateTicketAsync(slug, "Réveil unique", status: "Todo");
        await svc.ScheduleTicketAsync(slug, ticket.Id, DateTime.UtcNow.AddMinutes(-1), "Todo", "owner");
        var signals = 0;
        svc.TicketStatusChanged += (_, id, _, _) =>
        {
            if (id == ticket.Id) Interlocked.Increment(ref signals);
        };

        var results = await Task.WhenAll(
            svc.PromoteScheduledAsync(slug, ticket.Id),
            svc.PromoteScheduledAsync(slug, ticket.Id));

        Assert.Single(results, result => result is not null);
        Assert.Equal(1, signals);
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
    public async Task ConsumedSchedule_CannotResurrectDoneTicket_ButExplicitRescheduleStillWorks()
    {
        using var tmp = new TempDir();
        var (projects, svc, slug) = BuildSut(tmp);
        var now = new DateTime(2026, 8, 3, 9, 20, 0, DateTimeKind.Utc);
        var ticket = await svc.CreateTicketAsync(slug, "One-shot gate", status: "Todo");

        await svc.ScheduleTicketAsync(slug, ticket.Id, now.AddDays(-20), "Todo", "owner");
        var queuedDueIds = await svc.ListDueScheduledTicketIdsAsync(slug, now);
        Assert.Contains(ticket.Id, queuedDueIds);

        await svc.ReorderTicketAsync(slug, ticket.Id, "Done", 0);

        // Reproduce a consumed schedule left behind after the due-ticket id was queued.
        // The promotion must use the current column as the final authority, even when
        // stale scheduling fields still exist on the closed ticket.
        await using (var db = projects.GetProjectDb(slug))
        {
            var stale = await db.Tickets.FindAsync(ticket.Id);
            stale!.FireAt = now.AddDays(-20);
            stale.ScheduleTarget = "Todo";
            await db.SaveChangesAsync();
        }

        var dueAfterCompletion = await svc.ListDueScheduledTicketIdsAsync(slug, now);
        var stalePromotion = await svc.PromoteScheduledAsync(slug, ticket.Id);
        var completed = await svc.GetTicketAsync(slug, ticket.Id);

        Assert.DoesNotContain(ticket.Id, dueAfterCompletion);
        Assert.Null(stalePromotion);
        Assert.Equal("Done", completed!.Status);
        Assert.Equal(now.AddDays(-20), completed.FireAt);
        Assert.Equal("Todo", completed.ScheduleTarget);

        await svc.ScheduleTicketAsync(slug, ticket.Id, now.AddMinutes(-1), "Todo", "owner");
        var dueAfterExplicitReschedule = await svc.ListDueScheduledTicketIdsAsync(slug, now);
        var promoted = await svc.PromoteScheduledAsync(slug, ticket.Id);

        Assert.Contains(ticket.Id, dueAfterExplicitReschedule);
        Assert.Equal("Todo", promoted!.Status);
        Assert.Null(promoted.FireAt);
        Assert.Null(promoted.ScheduleTarget);
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
