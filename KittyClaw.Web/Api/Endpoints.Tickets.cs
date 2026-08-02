using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapTickets(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets", async (string slug, string? status, TicketPriority? priority, string? assignedTo, string? createdBy, string? search, int? parentId, TicketService ts) =>
            Results.Ok(await ts.ListTicketsAsync(slug, status, priority, assignedTo, createdBy, search, parentId)))
            .WithTags("Tickets")
            .Produces<List<TicketSummary>>();

        api.MapPost("/projects/{slug}/tickets", async (string slug, CreateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.CreateTicketAsync(
                    slug, req.Title, req.Description, req.CreatedBy, req.Status,
                    req.LabelIds, req.Priority, req.AssignedTo, req.ParentId,
                    req.PipelineId, req.ColumnId, req.BlocksParent);
                notifier.NotifyProjectUpdated(slug);
                return Results.Created($"/api/projects/{slug}/tickets/{ticket.Id}", ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPatch("/projects/{slug}/tickets/{id:int}", async (string slug, int id, UpdateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            // Every provided field — status included — is applied in ONE atomic write
            // (kittyclaw-front#113 follow-up, backport analysis §2.2): agents hand a ticket
            // off with a single {"status","assignedTo"} call and the automation engine can
            // never observe the transition half-applied. ExpectedStatus mismatches map to 409.
            try
            {
                var ticket = await ts.UpdateTicketAsync(slug, id, req.Title, req.Description, req.Author, req.Priority, req.AssignedTo, req.Status, req.ExpectedStatus, req.PipelineId, req.ColumnId, req.BlocksParent);
                if (ticket is not null && req.LabelIds is not null)
                    await ts.SetTicketLabelsAsync(slug, id, req.LabelIds);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (TicketTransitionConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message, actualStatus = ex.ActualStatus, expectedStatus = ex.ExpectedStatus });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/projects/{slug}/tickets/{id:int}/transfer", async (string slug, int id, TransferTicketRequest req, TicketTransferService transfers, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var result = await transfers.TransferAsync(slug, id, req.TargetProject, req.Actor);
                notifier.NotifyProjectUpdated(slug);
                notifier.NotifyProjectUpdated(req.TargetProject);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<TicketTransferResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/status", async (string slug, int id, MoveTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.MoveTicketAsync(slug, id, req.Status, req.Author);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/schedule", async (string slug, int id, ScheduleTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.ScheduleTicketAsync(slug, id, req.FireAt, req.TargetStatus ?? "Todo", req.Author);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapDelete("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var deleted = await ts.DeleteTicketAsync(slug, id);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Tickets")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // Sub-tickets
        api.MapPut("/projects/{slug}/tickets/{id:int}/parent", async (string slug, int id, SetParentRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var ok = await ts.SetParentAsync(slug, id, req.ParentId);
            if (ok) notifier.NotifyProjectUpdated(slug);
            return ok ? Results.NoContent() : Results.BadRequest(new { error = "Impossible d'associer ce sous-ticket." });
        }).WithTags("Tickets");

        api.MapDelete("/projects/{slug}/tickets/{id:int}/parent", async (string slug, int id, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var ok = await ts.UnparentAsync(slug, id);
            if (ok) notifier.NotifyProjectUpdated(slug);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithTags("Tickets");

        // Comments
        api.MapPost("/projects/{slug}/tickets/{id:int}/comments", async (string slug, int id, AddCommentRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                // Author defaults to "owner" when omitted (UI path); empty content is rejected.
                var comment = await ts.AddCommentAsync(slug, id, req.Content, req.Author ?? "owner");
                if (comment is not null) notifier.NotifyProjectUpdated(slug);
                return comment is null ? Results.NotFound() : Results.Created($"/api/projects/{slug}/tickets/{id}/comments/{comment.Id}", comment);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Comments");

        api.MapPatch("/projects/{slug}/tickets/{id:int}/comments/{commentId:int}", async (string slug, int id, int commentId, UpdateCommentRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ok = await ts.UpdateCommentAsync(slug, id, commentId, req.Content, req.Author ?? "owner");
                if (ok) notifier.NotifyProjectUpdated(slug);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Comments");

        api.MapDelete("/projects/{slug}/tickets/{id:int}/comments/{commentId:int}", async (string slug, int id, int commentId, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var ok = await ts.DeleteCommentAsync(slug, id, commentId);
            if (ok) notifier.NotifyProjectUpdated(slug);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithTags("Comments");

        // Activity
        api.MapGet("/projects/{slug}/tickets/{id:int}/activity", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            if (ticket is null) return Results.NotFound();
            var timeline = ticket.Comments
                .Select(c => new { at = c.CreatedAt, author = c.Author, type = "comment", text = c.Content, id = (int?)c.Id })
                .Cast<object>()
                .Concat(ticket.Activities
                    .Select(a => new { at = a.CreatedAt, author = a.Author, type = "event", text = a.Text, id = (int?)null })
                    .Cast<object>())
                .OrderBy(x => ((dynamic)x).at);
            return Results.Ok(timeline);
        }).WithTags("Activity");
    }

    private static void MapTicketReorder(RouteGroupBuilder api)
    {
        api.MapPatch("/projects/{slug}/tickets/{id:int}/reorder", async (string slug, int id, ReorderTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                await ts.ReorderTicketAsync(slug, id, req.Status, req.Index);
                notifier.NotifyProjectUpdated(slug);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
