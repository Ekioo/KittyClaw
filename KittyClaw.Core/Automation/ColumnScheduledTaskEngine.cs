using System.Collections.Concurrent;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Automation;

/// <summary>Runs durable cron action chains owned by columns, without launching an agent.</summary>
public sealed class ColumnScheduledTaskEngine(
    ProjectService projects,
    TicketService tickets,
    ColumnScheduledTaskService schedules,
    ColumnActionExecutor actions,
    ILogger<ColumnScheduledTaskEngine> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Task> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<(ColumnScheduledTask Task, ColumnScheduledTaskRun Run)>>
        _pausedRecoveries = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var project in await projects.ListProjectsAsync())
        {
            try
            {
                var recovered = await schedules.RecoverAsync(project.Slug);
                if (project.IsPaused)
                {
                    if (recovered.Count > 0) _pausedRecoveries[project.Slug] = recovered;
                    continue;
                }
                foreach (var item in recovered)
                    Start(project.Slug, item.Task, item.Run, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled column task recovery failed for project {Project}; continuing with other projects", project.Slug);
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var project in (await projects.ListProjectsAsync()).Where(project => !project.IsPaused))
                {
                    try
                    {
                        if (_pausedRecoveries.TryRemove(project.Slug, out var recovered))
                            foreach (var item in recovered)
                                Start(project.Slug, item.Task, item.Run, stoppingToken);
                        foreach (var item in await schedules.ClaimDueAsync(project.Slug, DateTime.UtcNow))
                            Start(project.Slug, item.Task, item.Run, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Scheduled column task synchronization failed for project {Project}; continuing with other projects", project.Slug);
                    }
                }
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled column task engine loop failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private void Start(string slug, ColumnScheduledTask task, ColumnScheduledTaskRun run, CancellationToken token)
    {
        var key = $"{slug}:{run.Id}";
        var work = RunAsync(slug, task, run, token);
        if (!_active.TryAdd(key, work)) return;
        _ = work.ContinueWith(_ => _active.TryRemove(key, out var _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync(
        string slug, ColumnScheduledTask task, ColumnScheduledTaskRun run, CancellationToken token)
    {
        string? error = null;
        try
        {
            await using var db = projects.GetProjectDb(slug);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            var column = await db.BoardColumns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == task.ColumnId, token)
                ?? throw new InvalidOperationException($"La colonne #{task.ColumnId} n’existe plus.");
            Ticket? ticket = null;
            if (task.TicketScope == ScheduledTaskTicketScope.FirstTicket)
            {
                var selected = (await tickets.ListTicketsAsync(slug))
                    .Where(item => item.ColumnId == column.Id ||
                        (item.ColumnId is null && item.PipelineId == column.PipelineId && item.Status == column.Name))
                    .OrderBy(item => item.SortOrder).ThenBy(item => item.Id).FirstOrDefault();
                if (selected is not null) ticket = await tickets.GetTicketAsync(slug, selected.Id);
            }

            foreach (var action in task.Actions)
            {
                if (run.CompletedActionIds.Contains(action.Id, StringComparer.OrdinalIgnoreCase)) continue;
                await schedules.BeginActionAsync(slug, run, action.Id);
                var result = await actions.ExecuteScheduledAsync(slug, task, run, column, ticket, action, token);
                if (!result.Succeeded)
                {
                    error = result.Error ?? "Échec inconnu de l’action planifiée.";
                    if (ticket is not null && action.FailureTargetColumnId is int targetId)
                    {
                        var target = await db.BoardColumns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == targetId, token);
                        if (target is not null && target.Id != column.Id)
                            await tickets.UpdateTicketAsync(slug, ticket.Id, author: task.Name,
                                status: target.Name, pipelineId: target.PipelineId, columnId: target.Id);
                    }
                    break;
                }
                await schedules.CompleteActionAsync(slug, run, action.Id);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(ex, "Scheduled task {TaskId} failed in {Project}", task.Id, slug);
        }
        await schedules.FinishRunAsync(slug, task, run, error);
    }
}
