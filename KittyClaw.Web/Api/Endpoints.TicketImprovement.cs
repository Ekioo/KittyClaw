using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

public sealed record AnalyzeTicketImprovementRequest(bool Split = false);
public sealed record TicketImprovementResponse(int TicketId, DateTime TicketUpdatedAt, TicketImprovement Proposal);
public sealed record ApplyTicketImprovementRequest(string Author, DateTimeOffset? ExpectedUpdatedAt,
    string Description, List<string> AcceptanceCriteria);

public static partial class Endpoints
{
    private static void MapTicketImprovement(RouteGroupBuilder api)
    {
        api.MapPost("/projects/{slug}/tickets/{id}/improvement/apply", HandleApplyTicketImprovement)
            .Produces(204).Produces(400).Produces(404).Produces(409)
            .WithTags("Tickets")
            .WithDescription("Atomically apply a reviewed description and acceptance criteria. expectedUpdatedAt must be ticketUpdatedAt from analysis. Rejects stale revisions with 409 without changes. Preserves evidence for unchanged criteria; new criteria start Pending. No model run or subticket creation.");
        api.MapPost("/projects/{slug}/tickets/{id}/improvement/analyze", HandleAnalyzeTicketImprovement)
            .Produces<TicketImprovementResponse>().Produces(404).Produces(422)
            .WithTags("Tickets")
            .WithDescription("Launch AI analysis to propose a deeper ticket description and observable acceptance criteria. Returns a proposal and the analyzed ticket revision without changing the ticket. Consumes model usage. Set split=true to request a breakdown instead.");
    }

    internal static async Task<IResult> HandleApplyTicketImprovement(string slug, int id,
        ApplyTicketImprovementRequest request, ProjectService projects, TicketService tickets,
        BoardUpdateNotifier notifier)
    {
        if (string.IsNullOrWhiteSpace(request.Author) || request.ExpectedUpdatedAt is null ||
            request.ExpectedUpdatedAt == DateTimeOffset.MinValue)
            return Results.BadRequest(new { error = "author and expectedUpdatedAt from the analysis are required." });
        // Validate the complete proposal before opening the write transaction.
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 100000 ||
            request.AcceptanceCriteria is null || request.AcceptanceCriteria.Count is < 1 or > 50 ||
            request.AcceptanceCriteria.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 10000))
            return Results.BadRequest(new { error = "A description and 1–50 non-empty acceptance criteria are required, within the supported size limits." });
        if (await projects.GetProjectAsync(slug) is null)
            return Results.NotFound(new { error = "Project not found." });
        var ticket = await tickets.GetTicketAsync(slug, id);
        if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });
        try
        {
            // The revision check runs inside the same transaction as both field writes.
            await tickets.ApplyImprovementAsync(slug, id, ticket.Description, ticket.AcceptanceCriteriaJson,
                request.Description, request.AcceptanceCriteria, request.ExpectedUpdatedAt.Value.UtcDateTime,
                request.Author.Trim());
            notifier.NotifyProjectUpdated(slug);
            return Results.NoContent();
        }
        catch (TicketImprovementConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    internal static async Task<IResult> HandleAnalyzeTicketImprovement(string slug, int id,
        AnalyzeTicketImprovementRequest request, ProjectService projects, TicketService tickets,
        ITicketImprovementAnalyzer analyzer, CancellationToken cancellationToken)
    {
        if (await projects.GetProjectAsync(slug) is null)
            return Results.NotFound(new { error = "Project not found." });
        var ticket = await tickets.GetTicketAsync(slug, id);
        if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });
        try
        {
            var proposal = await analyzer.AnalyzeAsync(slug, ticket, cancellationToken, request.Split);
            // SQLite restores DateTime without its Kind; ticket timestamps are stored in UTC.
            return Results.Ok(new TicketImprovementResponse(ticket.Id,
                DateTime.SpecifyKind(ticket.UpdatedAt, DateTimeKind.Utc), proposal));
        }
        catch (InvalidOperationException ex) { return Results.UnprocessableEntity(new { error = ex.Message }); }
        catch (JsonException) { return Results.UnprocessableEntity(new { error = "Ticket analysis returned invalid JSON. Please try again." }); }
    }
}
