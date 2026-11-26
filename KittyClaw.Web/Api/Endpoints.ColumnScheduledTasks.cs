using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapColumnScheduledTasks(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/scheduled-tasks", HandleListColumnScheduledTasks)
            .Produces<List<ColumnScheduledTaskResponse>>().Produces(404).WithTags("Pipelines");
        api.MapGet("/projects/{slug}/columns/{columnId:int}/scheduled-tasks", HandleGetColumnScheduledTasks)
            .Produces<List<ColumnScheduledTaskResponse>>().Produces(404).WithTags("Pipelines");
        api.MapPut("/projects/{slug}/columns/{columnId:int}/scheduled-tasks", HandleSaveColumnScheduledTasks)
            .Produces<List<ColumnScheduledTaskResponse>>().Produces(400).Produces(404)
            .WithSummary("Replace all scheduled tasks of a column. Preserve IDs; an empty list removes all tasks.")
            .WithTags("Pipelines");
        api.MapDelete("/projects/{slug}/columns/{columnId:int}/scheduled-tasks", HandleDeleteColumnScheduledTasks)
            .Produces(204).Produces(404).WithTags("Pipelines");
    }

    internal static async Task<IResult> HandleDeleteColumnScheduledTasks(string slug, int columnId,
        ProjectService projects, ColumnService columns, ColumnScheduledTaskService schedules, BoardUpdateNotifier notifier)
    {
        var result = await HandleSaveColumnScheduledTasks(slug, columnId, new SaveColumnScheduledTasksRequest([]),
            projects, columns, schedules, notifier);
        return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.NoContent() : result;
    }

    internal static Task<IResult> HandleGetColumnScheduledTasks(string slug, int columnId,
        ProjectService projects, ColumnService columns, ColumnScheduledTaskService schedules) =>
        HandleListColumnScheduledTasks(slug, columnId, projects, columns, schedules);

    internal static async Task<IResult> HandleListColumnScheduledTasks(string slug, int? columnId,
        ProjectService projects, ColumnService columns, ColumnScheduledTaskService schedules)
    {
        if (await projects.GetProjectAsync(slug) is null) return Results.NotFound(new { error = "Project or column not found." });
        if (columnId is int id && !(await columns.ListColumnsAsync(slug)).Any(column => column.Id == id))
            return Results.NotFound(new { error = "Project or column not found." });
        return Results.Ok((await schedules.ListAsync(slug, columnId)).Select(ScheduledTaskResponse).ToList());
    }

    internal static async Task<IResult> HandleSaveColumnScheduledTasks(string slug, int columnId,
        SaveColumnScheduledTasksRequest request, ProjectService projects, ColumnService columns,
        ColumnScheduledTaskService schedules, BoardUpdateNotifier notifier)
    {
        if (await projects.GetProjectAsync(slug) is null) return Results.NotFound(new { error = "Project or column not found." });
        if (!(await columns.ListColumnsAsync(slug)).Any(column => column.Id == columnId)) return Results.NotFound(new { error = "Project or column not found." });
        if (request.Tasks is null || request.Tasks.Any(task => task is null || task.Actions is null
            || task.Actions.Any(action => action is null || action.Action is null)))
            return Results.BadRequest(new { error = "Tasks and their action lists are required." });
        try
        {
            var saved = await schedules.SaveColumnAsync(slug, columnId, request.Tasks.Select(task => new ColumnScheduledTask
            {
                Id = task.Id ?? "", ColumnId = columnId, Name = task.Name, Enabled = task.Enabled,
                Cron = task.Cron, TimeZoneId = task.TimeZoneId ?? TimeZoneInfo.Local.Id,
                TicketScope = task.TicketScope, Actions = task.Actions,
            }).ToList());
            notifier.NotifyProjectUpdated(slug);
            return Results.Ok(saved.Select(ScheduledTaskResponse).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or NCrontab.CrontabException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static ColumnScheduledTaskResponse ScheduledTaskResponse(ColumnScheduledTask task) => new(
        task.Id, task.ColumnId, task.Name, task.Enabled, task.Cron, task.TimeZoneId, task.TicketScope,
        task.Actions, task.NextRunAt, task.LastRunAt, task.LastStatus, task.LastError);
}
