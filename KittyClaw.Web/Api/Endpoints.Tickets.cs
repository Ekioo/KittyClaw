using KittyClaw.Core.Evidence;
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
                if (req.SaturationOverride is not null
                    && !string.Equals(req.SaturationOverride.Kind, "recovery", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Unsupported saturation override kind.", code = "invalid_saturation_override" });
                var ticket = await ts.CreateTicketAsync(
                    slug, req.Title, req.Description, req.CreatedBy, req.Status,
                    req.LabelIds, req.Priority, req.AssignedTo, req.ParentId,
                    req.PipelineId, req.ColumnId, req.BlocksParent,
                    req.SaturationOverride is not null, req.SaturationOverride?.Reason);
                notifier.NotifyProjectUpdated(slug);
                return Results.Created($"/api/projects/{slug}/tickets/{ticket.Id}", ticket);
            }
            catch (TicketCreationSaturationException ex)
            {
                return Results.Conflict(new
                {
                    error = ex.Message,
                    code = TicketCreationSaturationException.ErrorCode,
                    blockedCount = ex.BlockedCount,
                    blockedLimit = ex.BlockedLimit,
                    blockedColumnIds = ex.BlockedColumnIds,
                    overrideKind = "recovery"
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets")
        .Produces<Ticket>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPatch("/projects/{slug}/tickets/{id:int}", async (string slug, int id, UpdateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            // Every provided field — status included — is applied in ONE atomic write
            // (kittyclaw-front#113 follow-up, backport analysis §2.2): agents hand a ticket
            // off with a single {"status","assignedTo"} call and the automation engine can
            // never observe the transition half-applied. ExpectedStatus mismatches map to 409.
            try
            {
                var ticket = await ts.UpdateTicketAsync(slug, id, req.Title, req.Description, req.Author, req.Priority, req.AssignedTo, req.Status, req.ExpectedStatus, req.PipelineId, req.ColumnId, req.BlocksParent,
                    enforceRouting: req.Status is not null || req.ColumnId is not null || req.PipelineId is not null);
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
                var ticket = await ts.UpdateTicketAsync(slug, id, author: req.Author, status: req.Status, enforceRouting: true);
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

        // Dependencies
        api.MapPost("/projects/{slug}/tickets/{id:int}/dependencies", async (string slug, int id, AddDependencyRequest req, TicketService ts) =>
        {
            try
            {
                var dep = await ts.AddDependencyAsync(slug, id, req.BlockedById);
                return Results.Created($"/api/projects/{slug}/tickets/{id}/dependencies/{dep.Id}", dep);
            }
            catch (DependencyValidationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message, reason = ex.Reason });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Dependencies")
        .Produces<TicketDependency>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapDelete("/projects/{slug}/tickets/{id:int}/dependencies/{depId:int}", async (string slug, int id, int depId, TicketService ts) =>
        {
            var removed = await ts.RemoveDependencyAsync(slug, id, depId);
            return removed
                ? Results.NoContent()
                : Results.NotFound(new
                {
                    error = "Dependency edge was not found for this ticket.",
                    reason = "dependency_not_found"
                });
        }).WithTags("Dependencies")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

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

    private static void MapTicketEvidence(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets/{id:int}/evidence", async (string slug, int id, EvidenceStore store, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            if (ticket is null) return Results.NotFound();
            var evidence = store.LoadTicket(id.ToString());
            if (evidence is null) return Results.NotFound();
            EvidenceStore.ApplyStaleness(evidence, EvidenceStore.DefaultStalenessThreshold);
            return Results.Ok(evidence);
        }).WithTags("Evidence");

        api.MapGet("/projects/{slug}/tickets/{id:int}/brief", async (string slug, int id, EvidenceStore store, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            if (ticket is null) return Results.NotFound();
            var evidence = store.LoadTicket(id.ToString());
            if (evidence is null) return Results.NotFound();
            EvidenceStore.ApplyStaleness(evidence, EvidenceStore.DefaultStalenessThreshold);
            return Results.Ok(DecisionBriefComposer.Compose(evidence));
        }).WithTags("Evidence");
    }

    private static void MapTicketReorder(RouteGroupBuilder api)
    {
        api.MapPatch("/projects/{slug}/tickets/{id:int}/reorder", async (string slug, int id, ReorderTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                await ts.ReorderTicketAsync(slug, id, req.Status, req.Index, enforceRouting: true);
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
