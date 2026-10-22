using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapColumns(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/columns", HandleListColumns).Produces<List<KittyClaw.Core.Models.BoardColumn>>()
            .WithTags("Columns");

        api.MapPost("/projects/{slug}/columns", HandleCreateColumn).Produces<KittyClaw.Core.Models.BoardColumn>(StatusCodes.Status201Created).WithTags("Columns");

        api.MapPatch("/projects/{slug}/columns/{columnId:int}", HandleUpdateColumn).WithTags("Columns");

        api.MapDelete("/projects/{slug}/columns/{columnId:int}", HandleDeleteColumn).WithTags("Columns");

        api.MapPatch("/projects/{slug}/columns/reorder", HandleReorderColumn).WithTags("Columns");
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleListColumns(string slug, int? pipelineId, ColumnService cs)
    {
        return Results.Ok(await cs.ListColumnsAsync(slug, pipelineId));
    }

    internal static async Task<IResult> HandleCreateColumn(string slug, CreateColumnRequest req, ColumnService cs, BoardUpdateNotifier notifier)
    {
        var column = await cs.CreateColumnAsync(slug, req.Name, req.Color, req.PipelineId, req.Role,
            userGuidance: req.UserGuidance);
        notifier.NotifyProjectUpdated(slug);
        return Results.Created($"/api/projects/{slug}/columns/{column.Id}", column);
    }

    internal static async Task<IResult> HandleUpdateColumn(string slug, int columnId, UpdateColumnRequest req, ColumnService cs, BoardUpdateNotifier notifier)
    {
        var column = await cs.UpdateColumnAsync(slug, columnId, req.Name, req.Color, req.Role, req.UserGuidance);
        if (column is not null) notifier.NotifyProjectUpdated(slug);
        return column is null ? Results.NotFound() : Results.Ok(column);
    }

    internal static async Task<IResult> HandleDeleteColumn(string slug, int columnId, string moveTicketsTo, ColumnService cs, BoardUpdateNotifier notifier)
    {
        var deleted = await cs.DeleteColumnAsync(slug, columnId, moveTicketsTo);
        if (deleted) notifier.NotifyProjectUpdated(slug);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> HandleReorderColumn(string slug, ReorderColumnRequest req, ColumnService cs, BoardUpdateNotifier notifier)
    {
        await cs.ReorderColumnAsync(slug, req.ColumnId, req.Index);
        notifier.NotifyProjectUpdated(slug);
        return Results.NoContent();
    }
}
