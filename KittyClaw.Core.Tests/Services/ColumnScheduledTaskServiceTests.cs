using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Tests.Services;

public sealed class ColumnScheduledTaskServiceTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public async Task Tasks_are_file_backed_and_claims_are_checkpointed()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Scheduled tasks");
        var columns = new ColumnService(projects);
        var column = (await columns.ListColumnsAsync(project.Slug))[0];
        var service = new ColumnScheduledTaskService(projects);
        var task = new ColumnScheduledTask
        {
            Id = "weekly-report", ColumnId = column.Id, Name = "Weekly report",
            Cron = "* * * * *", TimeZoneId = TimeZoneInfo.Utc.Id,
            Actions = [new ColumnProcessorAction("create", new CreateTicketActionSpec { Title = "Report" })],
        };

        var saved = await service.SaveColumnAsync(project.Slug, column.Id, [task]);
        var path = await service.GetDefinitionPathAsync(project.Slug, column.Id);
        Assert.Single(saved);
        Assert.True(File.Exists(path));
        Assert.Contains("weekly-report", await File.ReadAllTextAsync(path));

        var due = await service.ClaimDueAsync(project.Slug, DateTime.UtcNow.AddMinutes(2));
        var claimed = Assert.Single(due);
        await service.BeginActionAsync(project.Slug, claimed.Run, "create");
        await service.CompleteActionAsync(project.Slug, claimed.Run, "create");
        await service.FinishRunAsync(project.Slug, claimed.Task, claimed.Run, null);

        var row = Assert.Single(await service.ListAsync(project.Slug, column.Id));
        Assert.Equal("completed", row.LastStatus);
    }

    [Fact]
    public async Task Ticket_actions_need_a_ticket_scope_and_self_routes_are_rejected()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Invalid schedules");
        var column = (await new ColumnService(projects).ListColumnsAsync(project.Slug))[0];
        var service = new ColumnScheduledTaskService(projects);

        var task = new ColumnScheduledTask
        {
            Id = "invalid", ColumnId = column.Id, Name = "Invalid", Cron = "0 9 * * 1",
            TimeZoneId = TimeZoneInfo.Utc.Id,
            Actions = [new ColumnProcessorAction("comment", new AddCommentActionSpec { Content = "Hi" }, column.Id)],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveColumnAsync(project.Slug, column.Id, [task]));
    }

    [Fact]
    public async Task Deleting_a_column_removes_incoming_scheduled_failure_routes()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Schedule routes");
        var schedules = new ColumnScheduledTaskService(projects);
        var columns = new ColumnService(projects, scheduledTaskService: schedules);
        var board = await columns.ListColumnsAsync(project.Slug);
        var source = board[0];
        var target = board[1];
        await schedules.SaveColumnAsync(project.Slug, source.Id,
        [
            new ColumnScheduledTask
            {
                Id = "route", ColumnId = source.Id, Name = "Route", Cron = "0 9 * * 1",
                TimeZoneId = TimeZoneInfo.Utc.Id, TicketScope = ScheduledTaskTicketScope.FirstTicket,
                Actions = [new ColumnProcessorAction("script", new ExecutePowerShellActionSpec { Script = "exit 1" }, target.Id)],
            },
        ]);

        Assert.True(await columns.DeleteColumnAsync(project.Slug, target.Id, board[2].Name));

        var reloaded = Assert.Single(await schedules.ListAsync(project.Slug, source.Id));
        Assert.Null(Assert.Single(reloaded.Actions).FailureTargetColumnId);
    }

    [Fact]
    public async Task Background_sync_isolates_invalid_files_deduplicates_diagnostics_and_recovers()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Resilient schedules");
        var columns = await new ColumnService(projects).ListColumnsAsync(project.Slug);
        var logger = new RecordingLogger<ColumnScheduledTaskService>();
        var service = new ColumnScheduledTaskService(projects, logger);
        var invalidColumn = columns[0];
        var validColumn = columns[1];

        await service.SaveColumnAsync(project.Slug, invalidColumn.Id,
        [
            NewTask("recovering", invalidColumn.Id, enabled: false),
        ]);
        await service.SaveColumnAsync(project.Slug, validColumn.Id,
        [
            NewTask("healthy", validColumn.Id),
        ]);
        var invalidPath = await service.GetDefinitionPathAsync(project.Slug, invalidColumn.Id);
        var corrected = (await File.ReadAllTextAsync(invalidPath)).Replace("\"enabled\": false", "\"enabled\": true");

        await File.WriteAllTextAsync(invalidPath, "{ invalid json");
        var firstClaims = await service.ClaimDueForBackgroundAsync(project.Slug, DateTime.UtcNow.AddMinutes(2));
        Assert.Equal("healthy", Assert.Single(firstClaims).Task.Id);
        Assert.Single(logger.Warnings);

        await service.ClaimDueForBackgroundAsync(project.Slug, DateTime.UtcNow.AddMinutes(2));
        Assert.Single(logger.Warnings);
        await Assert.ThrowsAnyAsync<Exception>(() => service.ListAsync(project.Slug));

        await File.WriteAllTextAsync(invalidPath, "{ differently invalid json");
        await service.ClaimDueForBackgroundAsync(project.Slug, DateTime.UtcNow.AddMinutes(2));
        Assert.Equal(2, logger.Warnings.Count);

        await File.WriteAllTextAsync(invalidPath, corrected);
        var recoveredClaims = await service.ClaimDueForBackgroundAsync(project.Slug, DateTime.UtcNow.AddMinutes(2));
        Assert.Equal("recovering", Assert.Single(recoveredClaims).Task.Id);
        Assert.Equal(2, logger.Warnings.Count);
    }

    private static ColumnScheduledTask NewTask(string id, int columnId, bool enabled = true) => new()
    {
        Id = id,
        ColumnId = columnId,
        Name = id,
        Enabled = enabled,
        Cron = "* * * * *",
        TimeZoneId = TimeZoneInfo.Utc.Id,
        Actions = [new ColumnProcessorAction("create", new CreateTicketActionSpec { Title = id })],
    };

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    public void Dispose() => _temp.Dispose();
}
