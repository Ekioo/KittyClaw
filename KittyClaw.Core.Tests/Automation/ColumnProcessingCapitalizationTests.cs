using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnProcessingCapitalizationTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public async Task Persisted_lesson_is_injected_into_the_next_processor_profile()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Memory reinjection");
        var columns = new ColumnService(projects);
        var column = (await columns.ListColumnsAsync(project.Slug))[0];
        var skills = new ProjectSkillService(projects);
        var processors = new ColumnProcessorService(projects, skills);
        var processor = await processors.SaveAsync(project.Slug, column.Id, "Worker", "Work.", null,
            true, 20, [], [], []);
        var memory = new ColumnMemoryCapitalizationService(projects);
        const string lesson = "Read the recovered canonical index before the next business run starts.";
        Assert.Equal(MemoryCapitalizationStatus.Succeeded,
            (await memory.CapitalizeAsync(project.Slug, column.Id, "first-run", [lesson])).Status);
        var dispatcher = new ColumnAgentDispatcher(null!, projects, skills, processors);

        var nextProfile = await dispatcher.BuildProfileAsync(project.Slug, processor);

        Assert.Contains("## Persistent memory", nextProfile);
        Assert.Contains(lesson, nextProfile);
    }

    [Fact]
    public async Task Failed_capitalization_is_observable_and_retries_without_redispatch_before_routing()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Capitalization recovery");
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var skills = new ProjectSkillService(projects);
        var processors = new ColumnProcessorService(projects, skills);
        var executions = new ColumnExecutionService(projects, tickets);
        var pipeline = await pipelines.CreateAsync(project.Slug, "Delivery");
        var source = await columns.CreateColumnAsync(project.Slug, "Ready", pipelineId: pipeline.Id);
        var target = await columns.CreateColumnAsync(project.Slug, "Done", pipelineId: pipeline.Id,
            role: ColumnRole.Success);
        await processors.SaveAsync(project.Slug, source.Id, "Worker", "Work.", null,
            true, 20, [], [], [], maxAttempts: 3, retryBackoffSeconds: 1,
            defaultTargetColumnId: target.Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Learn then route", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var dispatcher = new CountingDispatcher(new ColumnAgentResult("completed", [], "Done.",
            Lessons: ["Keep the saved business result while recoverable memory persistence is retried."]));
        var memory = new PausedIndexWriteService(projects);
        var actions = new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance);
        using var engine = new ColumnProcessingEngine(projects, tickets, processors, executions,
            dispatcher, actions, memory, NullLogger<ColumnProcessingEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitForAsync(async () =>
            {
                var run = (await executions.ListAsync(project.Slug, ticket.Id)).SingleOrDefault();
                return run is { Status: ColumnExecutionStatus.Retrying, AgentCompleted: true,
                    CapitalizationStatus: MemoryCapitalizationStatus.RetryRequired };
            }));
            Assert.Equal(source.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
            Assert.Equal(1, dispatcher.Count);

            memory.Resume();
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            engine.Signal(project.Slug);

            Assert.True(await WaitForAsync(async () =>
            {
                var run = (await executions.ListAsync(project.Slug, ticket.Id)).SingleOrDefault();
                return run is { Status: ColumnExecutionStatus.Completed,
                    CapitalizationStatus: MemoryCapitalizationStatus.Succeeded };
            }));
            Assert.Equal(target.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
            Assert.Equal(1, dispatcher.Count);
            var index = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors",
                $"column-{source.Id}", "memory", "MEMORY.md");
            Assert.Contains("saved business result", await File.ReadAllTextAsync(index));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Validation_feedback_is_attributed_to_the_processor_that_routed_the_ticket_to_validation()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Downstream feedback attribution");
        var pipelines = new PipelineService(projects);
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var skills = new ProjectSkillService(projects);
        var processors = new ColumnProcessorService(projects, skills);
        var executions = new ColumnExecutionService(projects, tickets);
        var pipeline = await pipelines.CreateAsync(project.Slug, "Delivery");
        var implementation = await columns.CreateColumnAsync(project.Slug, "Implementation", pipelineId: pipeline.Id);
        var validation = await columns.CreateColumnAsync(project.Slug, "Validation", pipelineId: pipeline.Id);
        var correction = await columns.CreateColumnAsync(project.Slug, "Correction", pipelineId: pipeline.Id);
        await processors.SaveAsync(project.Slug, implementation.Id, "Implementer", "Implement.", null,
            true, 20, [], [], [], defaultTargetColumnId: validation.Id);
        await processors.SaveAsync(project.Slug, validation.Id, "Reviewer", "Validate.", null,
            true, 20, [], [], [], defaultTargetColumnId: correction.Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Reject once", status: implementation.Name,
            pipelineId: pipeline.Id, columnId: implementation.Id);
        const string feedback = "Test the index repair path when validation interrupts persistence between replacements.";
        var dispatcher = new RoutingDispatcher(new Dictionary<int, ColumnAgentResult>
        {
            [implementation.Id] = new("completed", [], "Implemented.", Lessons: []),
            [validation.Id] = new("changes_requested", [], "Atomicity missing.", Lessons: [feedback]),
        });
        var actions = new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance);
        using var engine = new ColumnProcessingEngine(projects, tickets, processors, executions,
            dispatcher, actions, new ColumnMemoryCapitalizationService(projects),
            NullLogger<ColumnProcessingEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitForAsync(async () =>
                (await tickets.GetTicketAsync(project.Slug, ticket.Id))?.ColumnId == correction.Id));
            var upstreamIndex = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors",
                $"column-{implementation.Id}", "memory", "MEMORY.md");
            Assert.Contains(feedback, await File.ReadAllTextAsync(upstreamIndex));
            Assert.Equal(2, dispatcher.Count);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    public void Dispose() => _temp.Dispose();

    private sealed class CountingDispatcher(ColumnAgentResult result) : IColumnAgentDispatcher
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
            return Task.FromResult(new ColumnDispatchResult(run, result, null));
        }
    }

    private sealed class RoutingDispatcher(IReadOnlyDictionary<int, ColumnAgentResult> results)
        : IColumnAgentDispatcher
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
            return Task.FromResult(new ColumnDispatchResult(run, results[processor.ColumnId], null));
        }
    }

    private sealed class PausedIndexWriteService(ProjectService projects)
        : ColumnMemoryCapitalizationService(projects)
    {
        private volatile bool _paused = true;

        public void Resume() => _paused = false;

        protected override Task WriteAtomicallyAsync(
            string path, string content, CancellationToken cancellationToken)
        {
            if (_paused && string.Equals(Path.GetFileName(path), "MEMORY.md", StringComparison.Ordinal))
                throw new IOException("Injected index persistence failure.");
            return base.WriteAtomicallyAsync(path, content, cancellationToken);
        }
    }
}
