using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnScheduledTaskEngineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduled_chain_runs_without_legacy_rules_and_recovers_completed_actions(bool interrupted)
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Scheduled chain");
        var tickets = new TicketService(projects, new MemberService(projects));
        var column = (await new ColumnService(projects).ListColumnsAsync(project.Slug))[0];
        var schedules = new ColumnScheduledTaskService(projects);
        var actions = new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance);
        var registry = new AgentRunRegistry();
        var task = new ColumnScheduledTask
        {
            Id = "scheduled-chain",
            ColumnId = column.Id,
            Name = "Scheduled chain",
            Cron = "* * * * *",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Actions = [Create("first"), Create("second")],
        };
        ColumnProcessorAction Create(string id) => new(id, new CreateTicketActionSpec
        {
            Title = id, Status = column.Name, SkipIfExists = false,
        });
        await schedules.SaveColumnAsync(project.Slug, column.Id, [task]);
        var agents = Path.Combine(projects.ResolveWorkspacePath(project), ".agents");
        await File.WriteAllTextAsync(Path.Combine(agents, "automations.json"),
            """{"automations":[{"enabled":true,"trigger":{"type":"removed-trigger"},"actions":[{"type":"removed-action"}]}]}""");

        if (interrupted)
        {
            var claimed = Assert.Single(await schedules.ClaimDueAsync(project.Slug, DateTime.UtcNow.AddMinutes(2)));
            await schedules.BeginActionAsync(project.Slug, claimed.Run, "first");
            Assert.True((await actions.ExecuteScheduledAsync(project.Slug, claimed.Task, claimed.Run,
                column, null, claimed.Task.Actions[0], CancellationToken.None)).Succeeded);
            await schedules.CompleteActionAsync(project.Slug, claimed.Run, "first");
        }
        else
        {
            await using var db = projects.GetProjectDb(project.Slug);
            var persisted = await db.ColumnScheduledTasks.SingleAsync();
            persisted.NextRunAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        using var engine = new ColumnScheduledTaskEngine(projects, tickets, schedules, actions,
            NullLogger<ColumnScheduledTaskEngine>.Instance, runs: registry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await engine.StartAsync(timeout.Token);
        try
        {
            while (Assert.Single(await schedules.ListAsync(project.Slug, column.Id)).LastStatus != "completed")
                await Task.Delay(25, timeout.Token);
        }
        finally { await engine.StopAsync(CancellationToken.None); }

        Assert.Equal(["first", "second"], (await tickets.ListTicketsAsync(project.Slug))
            .Select(ticket => ticket.Title).Order().ToArray());
        await using var verification = projects.GetProjectDb(project.Slug);
        var run = Assert.Single(await verification.ColumnScheduledTaskRuns.ToListAsync());
        Assert.Equal(ColumnScheduledTaskRunStatus.Completed, run.Status);
        Assert.Equal(["first", "second"], run.CompletedActionIds);
        Assert.Empty(registry.AllActive());
    }
}
