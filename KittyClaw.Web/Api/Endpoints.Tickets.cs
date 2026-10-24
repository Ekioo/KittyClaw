using KittyClaw.Core.Evidence;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapTickets(RouteGroupBuilder api)
    {
        api.MapPost("/projects/{slug}/tickets/{id:int}/focus", HandleFocusTicket)
            .WithTags("Tickets")
            .WithSummary("Focus a ticket card in connected kanban views of this project without opening its detail panel.")
            .Produces<TicketFocusResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/projects/{slug}/tickets", async (string slug, string? status, TicketPriority? priority, string? assignedTo, string? createdBy, string? search, int? parentId, TicketService ts) =>
            Results.Ok(await ts.ListTicketsAsync(slug, status, priority, assignedTo, createdBy, search, parentId)))
            .WithTags("Tickets")
            .Produces<List<TicketSummary>>();

        api.MapPost("/projects/{slug}/tickets", HandleCreateTicket).WithTags("Tickets")
        .WithSummary("Create a ticket; set focus=true to reveal its card in connected project kanban views without opening the detail panel.")
        .Produces<Ticket>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPatch("/projects/{slug}/tickets/{id:int}", HandleUpdateTicket).WithTags("Tickets")
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

        api.MapPost("/projects/{slug}/tickets/{id:int}/transfer", HandleTransferTicket).WithTags("Tickets")
        .WithSummary("Transfer a ticket to another project; set focus=true to highlight its card in the destination project's kanban without opening the detail panel.")
        .Produces<TicketTransferResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/status", HandleMoveTicket).WithTags("Tickets")
        .WithSummary("Move a ticket; set focus=true to highlight its card in the destination kanban column.")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/schedule", HandleScheduleTicket).WithTags("Tickets")
        .WithSummary("Schedule a ticket; set focus=true to highlight its card in Scheduled without opening the detail panel.")
        .Produces<Ticket>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapDelete("/projects/{slug}/tickets/{id:int}", HandleDeleteTicket).WithTags("Tickets")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/projects/{slug}/tickets/{id:int}/acceptance-criteria", async (string slug, int id, SaveAcceptanceCriterionRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try {
                var criterion = await ts.SaveAcceptanceCriterionAsync(slug, id, null, req);
                if (criterion is null) return Results.NotFound(new { error = "Ticket or criterion not found." });
                notifier.NotifyProjectUpdated(slug);
                return Results.Ok(criterion);
            } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Tickets").Produces<TicketAcceptanceCriterion>();
        api.MapPut("/projects/{slug}/tickets/{id:int}/acceptance-criteria/{criterionId}", async (string slug, int id, string criterionId, SaveAcceptanceCriterionRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try {
                var criterion = await ts.SaveAcceptanceCriterionAsync(slug, id, criterionId, req);
                if (criterion is null) return Results.NotFound(new { error = "Ticket or criterion not found." });
                notifier.NotifyProjectUpdated(slug);
                return Results.Ok(criterion);
            } catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Tickets").Produces<TicketAcceptanceCriterion>();
        api.MapDelete("/projects/{slug}/tickets/{id:int}/acceptance-criteria/{criterionId}", async (string slug, int id, string criterionId, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            if (!await ts.DeleteAcceptanceCriterionAsync(slug, id, criterionId)) return Results.NotFound(new { error = "Ticket or criterion not found." });
            notifier.NotifyProjectUpdated(slug);
            return Results.NoContent();
        }).WithTags("Tickets");

        // Sub-tickets
        api.MapPut("/projects/{slug}/tickets/{id:int}/parent", HandleSetTicketParent).WithTags("Tickets")
            .WithSummary("Attach a ticket to a parent; set focus=true in the body to highlight its card in the parent's kanban.");

        api.MapDelete("/projects/{slug}/tickets/{id:int}/parent", HandleRemoveTicketParent).WithTags("Tickets")
            .WithSummary("Detach a ticket from its parent; set query parameter focus=true to highlight its card in the main kanban.");

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
        }).WithTags("Comments")
        .Produces<Comment>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        api.MapPatch("/projects/{slug}/tickets/{id:int}/comments/{commentId:int}", HandleUpdateTicketComment).WithTags("Comments");

        api.MapDelete("/projects/{slug}/tickets/{id:int}/comments/{commentId:int}", HandleDeleteTicketComment).WithTags("Comments");

        // Dependencies
        api.MapPost("/projects/{slug}/tickets/{id:int}/dependencies", HandleAddTicketDependency).WithTags("Dependencies")
        .Produces<TicketDependency>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapDelete("/projects/{slug}/tickets/{id:int}/dependencies/{depId:int}", HandleRemoveTicketDependency).WithTags("Dependencies")
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

    internal static async Task<IResult> HandleCreateTicket(string slug, CreateTicketRequest req, TicketService ts,
        BoardUpdateNotifier notifier, BoardFocusService focus)
    {
        try
        {
            var ticket = await ts.CreateTicketAsync(
                slug, req.Title, req.Description, req.CreatedBy, req.Status,
                req.LabelIds, req.Priority, req.AssignedTo, req.ParentId,
                req.PipelineId, req.ColumnId, req.BlocksParent);
            notifier.NotifyProjectUpdated(slug);
            if (req.Focus) focus.Request(slug, ticket);
            return Results.Created($"/api/projects/{slug}/tickets/{ticket.Id}", ticket);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> HandleFocusTicket(string slug, int id, TicketService tickets, BoardFocusService focus, ProjectService projects)
    {
        if (await projects.GetProjectAsync(slug) is null) return Results.NotFound();
        var ticket = await tickets.GetTicketAsync(slug, id);
        if (ticket is null) return Results.NotFound();
        var url = focus.Request(slug, ticket);
        return Results.Ok(new TicketFocusResponse(ticket.Id, ticket.PipelineId, ticket.ColumnId, url));
    }

    private static void MapTicketEvidence(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/tickets/{id:int}/evidence", HandleGetTicketEvidence).WithTags("Evidence");

        api.MapGet("/projects/{slug}/tickets/{id:int}/brief", HandleGetTicketBrief).WithTags("Evidence");
    }

    private static void MapTicketReorder(RouteGroupBuilder api)
    {
        api.MapPatch("/projects/{slug}/tickets/{id:int}/reorder", HandleReorderTicket).WithTags("Tickets")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    // Shared application handlers used by REST endpoints and MCP tools.
    internal static async Task<IResult> HandleMoveTicket(string slug, int id, MoveTicketRequest req, TicketService ts,
        BoardUpdateNotifier notifier, BoardFocusService focus)
    {
        try
        {
            var ticket = await ts.UpdateTicketAsync(slug, id, author: req.Author, status: req.Status, enforceRouting: true);
            if (ticket is not null)
            {
                notifier.NotifyProjectUpdated(slug);
                if (req.Focus) focus.Request(slug, ticket);
            }
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    internal static async Task<IResult> HandleUpdateTicket(string slug, int id, UpdateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier, BoardFocusService focus)
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
            if (ticket is not null)
            {
                notifier.NotifyProjectUpdated(slug);
                if (req.Focus) focus.Request(slug, ticket);
            }
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
    }

    internal static async Task<IResult> HandleScheduleTicket(string slug, int id, ScheduleTicketRequest req, TicketService ts, BoardUpdateNotifier notifier, BoardFocusService focus)
    {
        try
        {
            var ticket = await ts.ScheduleTicketAsync(slug, id, req.FireAt, req.TargetStatus ?? "Todo", req.Author);
            if (ticket is not null)
            {
                notifier.NotifyProjectUpdated(slug);
                if (req.Focus) focus.Request(slug, ticket);
            }
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> HandleDeleteTicket(string slug, int id, TicketService ts, BoardUpdateNotifier notifier)
    {
        var deleted = await ts.DeleteTicketAsync(slug, id);
        if (deleted) notifier.NotifyProjectUpdated(slug);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> HandleSetTicketParent(string slug, int id, SetParentRequest req, TicketService ts, BoardUpdateNotifier notifier, BoardFocusService focus)
    {
        var ok = await ts.SetParentAsync(slug, id, req.ParentId);
        if (ok)
        {
            notifier.NotifyProjectUpdated(slug);
            if (req.Focus && await ts.GetTicketAsync(slug, id) is { } ticket)
                focus.Request(slug, ticket);
        }
        return ok ? Results.NoContent() : Results.BadRequest(new { error = "Impossible d'associer ce sous-ticket." });
    }

    internal static async Task<IResult> HandleRemoveTicketParent(string slug, int id, TicketService ts, BoardUpdateNotifier notifier, BoardFocusService focusService, bool focus = false)
    {
        var ok = await ts.UnparentAsync(slug, id);
        if (ok)
        {
            notifier.NotifyProjectUpdated(slug);
            if (focus && await ts.GetTicketAsync(slug, id) is { } ticket)
                focusService.Request(slug, ticket);
        }
        return ok ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> HandleAddTicketDependency(string slug, int id, AddDependencyRequest req, TicketService ts)
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
    }

    internal static async Task<IResult> HandleRemoveTicketDependency(string slug, int id, int depId, TicketService ts)
    {
        var removed = await ts.RemoveDependencyAsync(slug, id, depId);
        return removed
            ? Results.NoContent()
            : Results.NotFound(new
            {
                error = "Dependency edge was not found for this ticket.",
                reason = "dependency_not_found"
            });
    }

    internal static async Task<IResult> HandleUpdateTicketComment(string slug, int id, int commentId, UpdateCommentRequest req, TicketService ts, BoardUpdateNotifier notifier)
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
    }

    internal static async Task<IResult> HandleDeleteTicketComment(string slug, int id, int commentId, TicketService ts, BoardUpdateNotifier notifier)
    {
        var ok = await ts.DeleteCommentAsync(slug, id, commentId);
        if (ok) notifier.NotifyProjectUpdated(slug);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    internal static async Task<IResult> HandleReorderTicket(string slug, int id, ReorderTicketRequest req, TicketService ts, BoardUpdateNotifier notifier, BoardFocusService focus)
    {
        try
        {
            await ts.ReorderTicketAsync(slug, id, req.Status, req.Index, enforceRouting: true);
            notifier.NotifyProjectUpdated(slug);
            if (req.Focus && await ts.GetTicketAsync(slug, id) is { } ticket)
                focus.Request(slug, ticket);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> HandleTransferTicket(string slug, int id, TransferTicketRequest req, TicketTransferService transfers, BoardUpdateNotifier notifier,
        TicketService tickets, BoardFocusService focus)
    {
        try
        {
            var result = await transfers.TransferAsync(slug, id, req.TargetProject, req.Actor);
            var targetTicket = req.Focus ? await tickets.GetTicketAsync(result.TargetProject, result.TicketId) : null;
            notifier.NotifyProjectUpdated(slug);
            notifier.NotifyProjectUpdated(req.TargetProject);
            if (targetTicket is not null)
                focus.RequestTransfer(result.SourceProject, result.TargetProject, targetTicket);
            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> HandleGetTicketEvidence(string slug, int id, EvidenceStore store, TicketService ts)
    {
        var ticket = await ts.GetTicketAsync(slug, id);
        if (ticket is null) return Results.NotFound();
        var evidence = store.LoadTicket(slug, id.ToString());
        if (evidence is null) return Results.NotFound();
        EvidenceStore.ApplyStaleness(evidence, EvidenceStore.DefaultStalenessThreshold);
        return Results.Ok(evidence);
    }

    internal static async Task<IResult> HandleGetTicketBrief(string slug, int id, EvidenceStore store, TicketService ts)
    {
        var ticket = await ts.GetTicketAsync(slug, id);
        if (ticket is null) return Results.NotFound();
        var evidence = store.LoadTicket(slug, id.ToString());
        if (evidence is null) return Results.NotFound();
        EvidenceStore.ApplyStaleness(evidence, EvidenceStore.DefaultStalenessThreshold);
        return Results.Ok(DecisionBriefComposer.Compose(evidence));
    }
}
