using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapColumns(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/columns", async (string slug, ColumnService cs) =>
            Results.Ok(await cs.ListColumnsAsync(slug)))
            .WithTags("Columns");

        api.MapPost("/projects/{slug}/columns", async (string slug, CreateColumnRequest req, ColumnService cs, BoardUpdateNotifier notifier) =>
        {
            var column = await cs.CreateColumnAsync(slug, req.Name, req.Color);
            notifier.NotifyProjectUpdated(slug);
            return Results.Created($"/api/projects/{slug}/columns/{column.Id}", column);
        }).WithTags("Columns");

        api.MapPatch("/projects/{slug}/columns/{columnId:int}", async (string slug, int columnId, UpdateColumnRequest req, ColumnService cs, BoardUpdateNotifier notifier) =>
        {
            var column = await cs.UpdateColumnAsync(slug, columnId, req.Name, req.Color);
            if (column is not null) notifier.NotifyProjectUpdated(slug);
            return column is null ? Results.NotFound() : Results.Ok(column);
        }).WithTags("Columns");

        api.MapDelete("/projects/{slug}/columns/{columnId:int}", async (string slug, int columnId, string moveTicketsTo, ColumnService cs, BoardUpdateNotifier notifier) =>
        {
            var deleted = await cs.DeleteColumnAsync(slug, columnId, moveTicketsTo);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Columns");

        api.MapPatch("/projects/{slug}/columns/reorder", async (string slug, ReorderColumnRequest req, ColumnService cs, BoardUpdateNotifier notifier) =>
        {
            await cs.ReorderColumnAsync(slug, req.ColumnId, req.Index);
            notifier.NotifyProjectUpdated(slug);
            return Results.NoContent();
        }).WithTags("Columns");
    }
}
