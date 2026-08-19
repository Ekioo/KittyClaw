using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Non-regression tests for ticket #114: an internal project reload must keep every scheduled
/// (cron/interval) task registered and firing. The historical bug silently unregistered them
/// until the next full restart (3-day outage on bloomii). Reload remains an engine operation;
/// it is no longer exposed as a public automation-management endpoint.
/// </summary>
public class AutomationReloadTests
{
    private static async Task<(ProjectRuntimeManager Manager, TriggerStateStore State, AutomationStore Store, string Slug)>
        BuildAsync(string dataDir, string projectName)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync(projectName);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        var store = new AutomationStore(projects);
        var state = new TriggerStateStore(projects);
        var manager = new ProjectRuntimeManager(store, state, NullLogger.Instance);
        return (manager, state, store, project.Slug);
    }

    private static AutomationRule CronAutomation(string id, string cron = "0 7 * * *") => new()
    {
        Id = id,
        Name = id,
        Trigger = new IntervalTriggerSpec { Cron = cron },
    };

    [Fact]
    public async Task Reload_keeps_scheduled_task_registered_with_same_next_run()
    {
        using var tmp = new TempDir();
        var (manager, _, store, slug) = await BuildAsync(tmp.Path, "reload-test");
        await store.SaveAsync(slug, new AutomationConfig { Automations = { CronAutomation("daily") } });

        await manager.ReloadProjectAsync(slug);
        var before = manager.GetNextRunTimes(slug);
        var scheduled = Assert.Single(before);
        Assert.Equal("daily", scheduled.Key);
        Assert.NotNull(scheduled.Value);

        // The reload of the ticket: must not unregister nor reset the schedule.
        await manager.ReloadProjectAsync(slug);
        var after = manager.GetNextRunTimes(slug);
        Assert.Equal(scheduled.Value, Assert.Single(after).Value);
    }

    [Fact]
    public async Task Reload_restores_overdue_schedule_that_still_fires_without_restart()
    {
        using var tmp = new TempDir();
        var (manager, state, store, slug) = await BuildAsync(tmp.Path, "reload-overdue-test");
        await store.SaveAsync(slug, new AutomationConfig { Automations = { CronAutomation("daily") } });
        await manager.ReloadProjectAsync(slug);

        // Simulate a missed occurrence (engine down at the scheduled moment), then a reload.
        var overdueAt = DateTime.UtcNow.AddHours(-3);
        await state.SetNextRunAtAsync(slug, "daily", overdueAt);
        await manager.ReloadProjectAsync(slug);

        // The rebuilt trigger must carry the persisted overdue time — meaning the next engine
        // tick fires it (catch-up) instead of the schedule being lost until restart.
        Assert.Equal(overdueAt, manager.GetNextRunTimes(slug)["daily"]);
        var rt = manager.GetRuntime(slug);
        var firings = await rt.Triggers["daily"].EvaluateAsync(MinimalContext(tmp.Path, slug), CancellationToken.None);
        Assert.Single(firings);
    }

    [Fact]
    public async Task Reload_of_one_project_does_not_touch_other_projects()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var a = await projects.CreateProjectAsync("proj-a");
        var b = await projects.CreateProjectAsync("proj-b");
        foreach (var p in new[] { a, b })
            Directory.CreateDirectory(Path.Combine(projects.ResolveWorkspacePath(p), ".agents"));
        var store = new AutomationStore(projects);
        var manager = new ProjectRuntimeManager(store, new TriggerStateStore(projects), NullLogger.Instance);
        await store.SaveAsync(a.Slug, new AutomationConfig { Automations = { CronAutomation("auto-a") } });
        await store.SaveAsync(b.Slug, new AutomationConfig { Automations = { CronAutomation("auto-b") } });
        await manager.ReloadProjectAsync(a.Slug);
        await manager.ReloadProjectAsync(b.Slug);

        var bTriggersBefore = manager.GetRuntime(b.Slug).Triggers;
        var bNextBefore = manager.GetNextRunTimes(b.Slug)["auto-b"];

        await manager.ReloadProjectAsync(a.Slug);

        Assert.Same(bTriggersBefore, manager.GetRuntime(b.Slug).Triggers);
        Assert.Equal(bNextBefore, manager.GetNextRunTimes(b.Slug)["auto-b"]);
    }

    [Fact]
    public async Task Reload_reports_invalid_file_and_keeps_previous_runtime()
    {
        using var tmp = new TempDir();
        var (manager, _, store, slug) = await BuildAsync(tmp.Path, "reload-invalid-test");
        await store.SaveAsync(slug, new AutomationConfig { Automations = { CronAutomation("daily") } });

        var first = await manager.ReloadProjectAsync(slug);
        Assert.True(first.Success);
        var runtimeBefore = manager.GetRuntime(slug).Config;

        var (_, _, configPath, _) = await store.LoadWithStampAsync(slug);
        await File.WriteAllTextAsync(configPath, "{ invalid json");

        var failed = await manager.ReloadProjectAsync(slug);

        Assert.False(failed.Success);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
        Assert.Same(runtimeBefore, manager.GetRuntime(slug).Config);
        Assert.Contains("daily", manager.GetNextRunTimes(slug).Keys);
    }

    [Fact]
    public async Task Health_snapshot_reports_registered_and_overdue_tasks()
    {
        using var tmp = new TempDir();
        var (manager, state, store, slug) = await BuildAsync(tmp.Path, "health-test");
        Assert.Null(manager.GetProjectHealth(slug));

        await store.SaveAsync(slug, new AutomationConfig
        {
            Automations =
            {
                CronAutomation("daily"),
                new AutomationRule { Id = "on-column", Trigger = new TicketInColumnTriggerSpec { Columns = { "Todo" } } },
                new AutomationRule { Id = "disabled", Enabled = false, Trigger = new IntervalTriggerSpec { Cron = "0 8 * * *" } },
            },
        });
        await state.SetNextRunAtAsync(slug, "daily", DateTime.UtcNow.AddHours(-3));
        await manager.ReloadProjectAsync(slug);

        var health = manager.GetProjectHealth(slug);
        Assert.NotNull(health);
        Assert.Equal(3, health!.AutomationCount);
        Assert.Equal(2, health.EnabledCount);
        Assert.Equal(1, health.ScheduledCount);   // only the enabled cron automation
        Assert.Equal(1, health.OverdueCount);     // its NextRunAt is 3h in the past
        Assert.NotNull(health.NextRunAt);
        Assert.Null(health.LastFiredAt);          // nothing dispatched in this test
    }

    private static KittyClaw.Core.Automation.Triggers.TriggerContext MinimalContext(string dataDir, string slug)
    {
        var projects = new ProjectService(dataDir);
        var members = new MemberService(projects);
        return new()
        {
            ProjectSlug = slug,
            WorkspacePath = dataDir,
            Automation = CronAutomation("daily"),
            Tickets = new TicketService(projects, members),
            Members = members,
            Sessions = new SessionRegistry(),
            Runs = new AgentRunRegistry(),
            Now = DateTime.UtcNow,
        };
    }
}
