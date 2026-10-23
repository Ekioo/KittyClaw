using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnResumeRegressionTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Theory]
    [InlineData(ColumnExecutionStatus.Failed, false)]
    [InlineData(ColumnExecutionStatus.Retrying, false)]
    [InlineData(ColumnExecutionStatus.WaitingForChildren, false)]
    [InlineData(ColumnExecutionStatus.Failed, true)]
    public async Task Manual_return_to_Todo_releases_old_claim_and_consumes_durable_owner_feedback(
        ColumnExecutionStatus status, bool reorder)
    {
        var f = await Setup();
        var old = (await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow))!;
        var result = new ColumnAgentResult("integration_ready", [], "Delivered artifact; integration reservation retained.",
            Lessons: ["Keep delivered artifacts and reservations when memory publication is refused."]);
        await f.Executions.SaveAgentResultAsync(f.Slug, old, result, f.Worker);
        await f.Executions.SetCapitalizationAsync(f.Slug, old, MemoryCapitalizationStatus.RetryRequired, "Git hook rejected memory");
        await using (var db = f.Projects.GetProjectDb(f.Slug))
        {
            var row = (await db.ColumnExecutions.FindAsync(old.Id))!;
            row.Status = status;
            await db.SaveChangesAsync();
        }
        await f.Tickets.AddCommentAsync(f.Slug, f.Ticket.Id, "I approve the delivered artifact; proceed to integration.", "owner");
        if (reorder) await f.Tickets.ReorderTicketAsync(f.Slug, f.Ticket.Id, f.Todo.Name, 0);
        else await f.Tickets.MoveTicketAsync(f.Slug, f.Ticket.Id, f.Todo.Name);

        // New service instances model startup: no in-memory owner-feedback signal survives.
        var executions = new ColumnExecutionService(f.Projects, f.Tickets);
        await executions.RecoverInterruptedAsync(f.Slug);
        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            executions.ClaimNextAsync(f.Slug, f.Qualifier, DateTime.UtcNow))));
        var resumed = Assert.Single(claims.OfType<ColumnExecution>());
        Assert.Equal("owner-feedback", resumed.TriggerSignalType);
        var latest = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.Comments.Last();
        Assert.Equal(latest.Id, resumed.TriggerOwnerCommentId);
        Assert.Null(await executions.ClaimNextAsync(f.Slug, f.Qualifier, DateTime.UtcNow));
        var archived = (await executions.ListAsync(f.Slug, f.Ticket.Id)).Single(e => e.Id == old.Id);
        Assert.Equal(ColumnExecutionStatus.Cancelled, archived.Status);
        Assert.Equal(old.AgentResultJson, archived.AgentResultJson);
        Assert.Equal(MemoryCapitalizationStatus.RetryRequired, archived.CapitalizationStatus);
        Assert.Equal("Git hook rejected memory", archived.CapitalizationError);
        await executions.CompleteAsync(f.Slug, old, f.Worker, result, "Worker");
        Assert.Equal(f.Todo.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        var dispatcher = new ColumnAgentDispatcher(null!, f.Projects, new ProjectSkillService(f.Projects), f.Processors, null!);
        var history = await dispatcher.BuildDeliveryHistoryAsync(f.Slug, f.Ticket.Id, resumed.Id);
        Assert.Contains("integration_ready", history);
        Assert.Contains("integration reservation retained", history);
    }

    [Fact]
    public async Task Running_claim_keeps_reservation_until_superseded_worker_stops_even_after_round_trip_and_restart()
    {
        var f = await Setup();
        var old = (await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow))!;
        await f.Tickets.MoveTicketAsync(f.Slug, f.Ticket.Id, f.Todo.Name);
        await f.Tickets.MoveTicketAsync(f.Slug, f.Ticket.Id, "Worker");
        Assert.Null(await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow));
        await f.Executions.RecoverInterruptedAsync(f.Slug);
        await f.Executions.RecoverInterruptedAsync(f.Slug);
        var next = await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow.AddMinutes(1));
        Assert.NotNull(next);
        Assert.NotEqual(old.Id, next.Id);
        Assert.Null(await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow.AddMinutes(1)));
        Assert.Equal(ColumnExecutionStatus.Cancelled,
            (await f.Executions.ListAsync(f.Slug, f.Ticket.Id)).Single(e => e.Id == old.Id).Status);
    }

    [Fact]
    public async Task Restart_after_saved_business_result_routes_once_and_preserves_failed_memory_without_dispatch()
    {
        var f = await Setup();
        var old = (await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow))!;
        await f.Executions.SaveAgentResultAsync(f.Slug, old,
            new("integration_ready", [], "Delivered.", Lessons: ["The already delivered work must survive a refused memory checkpoint."]), f.Worker);
        await f.Executions.RecoverInterruptedAsync(f.Slug);
        await f.Executions.RecoverInterruptedAsync(f.Slug);
        var recovered = (await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow.AddMinutes(1)))!;
        var dispatcher = new NoDispatch();
        using var engine = new ColumnProcessingEngine(f.Projects, f.Tickets, f.Processors, f.Executions,
            dispatcher, new(f.Projects, f.Tickets, NullLogger<ColumnActionExecutor>.Instance),
            new FailingMemory(f.Projects), NullLogger<ColumnProcessingEngine>.Instance);
        await engine.ProcessAsync(f.Slug, f.Worker, recovered, CancellationToken.None);
        var completed = Assert.Single(await f.Executions.ListAsync(f.Slug, f.Ticket.Id));
        Assert.Equal(ColumnExecutionStatus.Completed, completed.Status);
        Assert.Equal("integration_ready", completed.Outcome);
        Assert.Equal(MemoryCapitalizationStatus.RetryRequired, completed.CapitalizationStatus);
        Assert.Equal(1, completed.Attempt);
        Assert.Equal(f.Principal.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        await f.Executions.RecoverInterruptedAsync(f.Slug);
        Assert.Null(await f.Executions.ClaimNextAsync(f.Slug, f.Worker, DateTime.UtcNow));
    }

    private async Task<Fixture> Setup()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Resume regression");
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var todo = await columns.CreateColumnAsync(project.Slug, "Todo");
        var worker = await columns.CreateColumnAsync(project.Slug, "Worker");
        var principal = await columns.CreateColumnAsync(project.Slug, "Principal");
        var p = await processors.SaveAsync(project.Slug, worker.Id, "Worker", "Deliver.", null,
            true, 10, [], [], [], defaultTargetColumnId: principal.Id);
        var q = await processors.SaveAsync(project.Slug, todo.Id, "Qualifier", "Continue from existing deliveries.", null,
            true, 10, [], [], [], defaultTargetColumnId: principal.Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Preserve delivered work", status: worker.Name, columnId: worker.Id);
        return new(projects, project.Slug, tickets, processors, new(projects, tickets), todo, principal, p, q, ticket);
    }

    public void Dispose() => _temp.Dispose();
    private sealed record Fixture(ProjectService Projects, string Slug, TicketService Tickets,
        ColumnProcessorService Processors, ColumnExecutionService Executions, BoardColumn Todo,
        BoardColumn Principal, ColumnProcessor Worker, ColumnProcessor Qualifier, Ticket Ticket);
    private sealed class NoDispatch : IColumnAgentDispatcher
    {
        public Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution,
            Ticket ticket, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Delivered work was dispatched twice.");
    }
    private sealed class FailingMemory(ProjectService projects) : ColumnMemoryCapitalizationService(projects)
    {
        protected override Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
            => throw new IOException("Memory persistence is temporarily unavailable.");
    }
}
