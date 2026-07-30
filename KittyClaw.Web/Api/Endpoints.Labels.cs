using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapProjectLabels(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/labels", async (string slug, LabelService ls) =>
            Results.Ok(await ls.ListLabelsAsync(slug)))
            .WithTags("Labels");

        api.MapPost("/projects/{slug}/labels", async (string slug, CreateLabelRequest req, LabelService ls) =>
        {
            var label = await ls.CreateLabelAsync(slug, req.Name, req.Color);
            return Results.Created($"/api/projects/{slug}/labels/{label.Id}", label);
        }).WithTags("Labels");

        api.MapDelete("/projects/{slug}/labels/{labelId:int}", async (string slug, int labelId, LabelService ls, BoardUpdateNotifier notifier) =>
        {
            var deleted = await ls.DeleteLabelAsync(slug, labelId);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Labels");

        api.MapPatch("/projects/{slug}/labels/{labelId:int}", async (string slug, int labelId, UpdateLabelRequest req, LabelService ls, BoardUpdateNotifier notifier) =>
        {
            var label = await ls.UpdateLabelAsync(slug, labelId, req.Name, req.Color);
            if (label is not null) notifier.NotifyProjectUpdated(slug);
            return label is null ? Results.NotFound() : Results.Ok(label);
        }).WithTags("Labels");
    }

    private static void MapTicketLabels(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets/{id:int}/labels", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket.Labels);
        }).WithTags("Labels");

        api.MapPut("/projects/{slug}/tickets/{id:int}/labels", async (string slug, int id, SetTicketLabelsRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var ok = await ts.SetTicketLabelsAsync(slug, id, req.LabelIds);
            if (ok) notifier.NotifyProjectUpdated(slug);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithTags("Labels");

        // Incremental add/remove by name — the concurrent-writer-safe primitive (§2.3):
        // two agents adding different labels both keep theirs, unlike the replace-all
        // PUT above (kept for the UI). Returns the resulting label list.
        api.MapPatch("/projects/{slug}/tickets/{id:int}/labels", async (string slug, int id, PatchTicketLabelsRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.PatchTicketLabelsAsync(slug, id, req.Add ?? [], req.Remove ?? [], req.Author);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket.Labels);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Labels")
        .Produces<List<Label>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
