using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class PausedProjectRestartRecoveryTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public async Task Column_execution_is_recovered_even_when_project_starts_paused()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Paused processor recovery");
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
        var skill = await skills.CreateAsync(project.Slug, "Recovery", "Recover safely.");
        var processor = await processors.SaveAsync(project.Slug, source.Id, "Worker", "Work.", null,
            true, 20, [skill.Slug], [skill.Slug], [skill.Slug], defaultTargetColumnId: target.Id);
        await tickets.CreateTicketAsync(project.Slug, "Interrupted", status: source.Name,
            pipelineId: pipeline.Id, columnId: source.Id);
        var claimed = await executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow);
        await projects.TogglePauseAsync(project.Slug);

        var actions = new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance);
        using var engine = new ColumnProcessingEngine(projects, tickets, processors, executions,
            new NeverDispatch(), actions, NullLogger<ColumnProcessingEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            var recovered = await WaitForAsync(async () =>
                (await executions.ListAsync(project.Slug)).SingleOrDefault()?.Status == ColumnExecutionStatus.Retrying);

            Assert.True(recovered);
            var execution = Assert.Single(await executions.ListAsync(project.Slug));
            Assert.Equal(claimed!.Id, execution.Id);
            Assert.Equal(0, execution.Attempt);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduled_run_waits_while_paused_and_resumes_after_unpause()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Paused schedule recovery");
        var tickets = new TicketService(projects, new MemberService(projects));
        var column = (await new ColumnService(projects).ListColumnsAsync(project.Slug))[0];
        var schedules = new ColumnScheduledTaskService(projects);
        await schedules.SaveColumnAsync(project.Slug, column.Id,
        [
            new ColumnScheduledTask
            {
                Id = "resume-after-pause",
                ColumnId = column.Id,
                Name = "Resume after pause",
                Cron = "* * * * *",
                TimeZoneId = TimeZoneInfo.Utc.Id,
                Actions =
                [
                    new ColumnProcessorAction("create", new CreateTicketActionSpec
                    {
                        Title = "Recovered scheduled work",
                        Status = "Todo",
                    }),
                ],
            },
        ]);
        Assert.Single(await schedules.ClaimDueAsync(project.Slug, DateTime.UtcNow.AddMinutes(2)));
        await projects.TogglePauseAsync(project.Slug);

        var actions = new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance);
        using var engine = new ColumnScheduledTaskEngine(projects, tickets, schedules, actions,
            NullLogger<ColumnScheduledTaskEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200);
            Assert.Empty(await tickets.ListTicketsAsync(project.Slug));

            await projects.TogglePauseAsync(project.Slug);
            var resumed = await WaitForAsync(async () =>
                (await tickets.ListTicketsAsync(project.Slug))
                    .Any(ticket => ticket.Title == "Recovered scheduled work"), TimeSpan.FromSeconds(12));

            Assert.True(resumed);
            Assert.Single(await tickets.ListTicketsAsync(project.Slug),
                ticket => ticket.Title == "Recovered scheduled work");
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    public void Dispose() => _temp.Dispose();

    private sealed class NeverDispatch : IColumnAgentDispatcher
    {
        public Task<ColumnDispatchResult> DispatchAsync(
            string projectSlug, ColumnProcessor processor, ColumnExecution execution,
            Ticket ticket, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A paused project must not dispatch during startup recovery.");
    }
}
