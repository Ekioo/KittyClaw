using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnProcessingPeriodicSweepTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public async Task Busy_project_signals_do_not_starve_a_due_retry_in_another_project()
    {
        var projects = new ProjectService(_temp.Path);
        var quiet = await projects.CreateProjectAsync("Quiet retry project");
        var noisy = await projects.CreateProjectAsync("Noisy project");
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var skills = new ProjectSkillService(projects);
        var processors = new ColumnProcessorService(projects, skills);
        var executions = new ColumnExecutionService(projects, tickets);
        var pipeline = await pipelines.CreateAsync(quiet.Slug, "Delivery");
        var source = await columns.CreateColumnAsync(quiet.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await columns.CreateColumnAsync(quiet.Slug, "Done", pipelineId: pipeline.Id,
            role: ColumnRole.Success);
        var processor = await processors.SaveAsync(quiet.Slug, source.Id, "Worker", "Work.", null,
            true, 20, [], [], [], maxAttempts: 3, retryBackoffSeconds: 1,
            defaultTargetColumnId: target.Id);
        var ticket = await tickets.CreateTicketAsync(quiet.Slug, "Retry me", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var execution = await executions.ClaimNextAsync(quiet.Slug, processor, DateTime.UtcNow);
        Assert.NotNull(execution);
        await executions.FailAttemptAsync(quiet.Slug, execution, processor, "Transient failure.", "Worker");

        var dispatcher = new CountingDispatcher();
        var actions = new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance);
        using var engine = new ColumnProcessingEngine(projects, tickets, processors, executions,
            dispatcher, actions, new ColumnMemoryCapitalizationService(projects),
            NullLogger<ColumnProcessingEngine>.Instance)
        {
            // Signals below arrive every ~20 ms, far under this interval, so a sweep gated on
            // "pending queue empty" would never fire while the noisy project stays active.
            PeriodicSweepInterval = TimeSpan.FromMilliseconds(250),
        };

        await engine.StartAsync(CancellationToken.None);
        using var noisySignals = new CancellationTokenSource();
        var signalTask = Task.Run(async () =>
        {
            while (!noisySignals.IsCancellationRequested)
            {
                engine.Signal(noisy.Slug);
                await Task.Delay(20, noisySignals.Token);
            }
        });
        try
        {
            Assert.True(await WaitForAsync(async () =>
                (await executions.ListAsync(quiet.Slug, ticket.Id)).Single().Status
                    == ColumnExecutionStatus.Completed));
            Assert.Equal(1, dispatcher.Count);
            Assert.Equal(target.Id, (await tickets.GetTicketAsync(quiet.Slug, ticket.Id))!.ColumnId);
        }
        finally
        {
            noisySignals.Cancel();
            try { await signalTask; } catch (OperationCanceledException) { }
            await engine.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    public void Dispose() => _temp.Dispose();

    private sealed class CountingDispatcher : IColumnAgentDispatcher
    {
        public int Count { get; private set; }

        public Task<ColumnDispatchResult> DispatchAsync(
            string projectSlug, ColumnProcessor processor, ColumnExecution execution,
            Ticket ticket, CancellationToken cancellationToken)
        {
            Count++;
            var run = new AgentRun
            {
                RunId = execution.Id,
                ProjectSlug = projectSlug,
                TicketId = ticket.Id,
                AgentName = processor.Name,
                SkillFile = "processor",
                ConcurrencyGroup = $"column-{processor.ColumnId}",
                StartedAt = DateTime.UtcNow,
                Status = AgentRunStatus.Completed,
            };
            return Task.FromResult(new ColumnDispatchResult(
                run, new ColumnAgentResult("completed", [], "Done."), null));
        }
    }
}
