using Todo.Core.Models;
using Todo.Core.Services;

namespace Todo.Web.Api;

public static class Endpoints
{
    public static void MapTodoApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Statuses
        api.MapGet("/statuses", () =>
            Results.Ok(Enum.GetNames<TicketStatus>()))
            .WithTags("Statuses");

        // Projects
        api.MapGet("/projects", async (ProjectService ps) =>
            Results.Ok(await ps.ListProjectsAsync()))
            .WithTags("Projects");

        api.MapPost("/projects", async (CreateProjectRequest req, ProjectService ps) =>
        {
            var project = await ps.CreateProjectAsync(req.Name);
            return Results.Created($"/api/projects/{project.Slug}", project);
        }).WithTags("Projects");

        api.MapGet("/projects/{slug}", async (string slug, ProjectService ps) =>
        {
            var project = await ps.GetProjectAsync(slug);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapDelete("/projects/{slug}", async (string slug, ProjectService ps) =>
        {
            var deleted = await ps.DeleteProjectAsync(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Projects");

        // Tickets
        api.MapGet("/projects/{slug}/tickets", async (string slug, TicketStatus? status, TicketPriority? priority, string? assignedTo, string? createdBy, string? search, TicketService ts) =>
            Results.Ok(await ts.ListTicketsAsync(slug, status, priority, assignedTo, createdBy, search)))
            .WithTags("Tickets");

        api.MapPost("/projects/{slug}/tickets", async (string slug, CreateTicketRequest req, TicketService ts) =>
        {
            var ticket = await ts.CreateTicketAsync(slug, req.Title, req.Description, req.CreatedBy, req.Status, req.LabelIds, req.Priority, req.AssignedTo);
            return Results.Created($"/api/projects/{slug}/tickets/{ticket.Id}", ticket);
        }).WithTags("Tickets");

        api.MapPatch("/projects/{slug}/tickets/{id:int}", async (string slug, int id, UpdateTicketRequest req, TicketService ts) =>
        {
            var ticket = await ts.UpdateTicketAsync(slug, id, req.Title, req.Description, req.Author, req.Priority, req.AssignedTo);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).WithTags("Tickets");

        api.MapGet("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).WithTags("Tickets");

        api.MapPatch("/projects/{slug}/tickets/{id:int}/status", async (string slug, int id, MoveTicketRequest req, TicketService ts) =>
        {
            var ticket = await ts.MoveTicketAsync(slug, id, req.Status);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).WithTags("Tickets");

        api.MapDelete("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts) =>
        {
            var deleted = await ts.DeleteTicketAsync(slug, id);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Tickets");

        // Comments
        api.MapPost("/projects/{slug}/tickets/{id:int}/comments", async (string slug, int id, AddCommentRequest req, TicketService ts) =>
        {
            var comment = await ts.AddCommentAsync(slug, id, req.Content, req.Author);
            return comment is null ? Results.NotFound() : Results.Created($"/api/projects/{slug}/tickets/{id}", comment);
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

        // Labels (project-level)
        api.MapGet("/projects/{slug}/labels", async (string slug, LabelService ls) =>
            Results.Ok(await ls.ListLabelsAsync(slug)))
            .WithTags("Labels");

        api.MapPost("/projects/{slug}/labels", async (string slug, CreateLabelRequest req, LabelService ls) =>
        {
            var label = await ls.CreateLabelAsync(slug, req.Name, req.Color);
            return Results.Created($"/api/projects/{slug}/labels/{label.Id}", label);
        }).WithTags("Labels");

        api.MapDelete("/projects/{slug}/labels/{labelId:int}", async (string slug, int labelId, LabelService ls) =>
        {
            var deleted = await ls.DeleteLabelAsync(slug, labelId);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Labels");

        // Labels (ticket-level)
        api.MapGet("/projects/{slug}/tickets/{id:int}/labels", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket.Labels);
        }).WithTags("Labels");

        api.MapPut("/projects/{slug}/tickets/{id:int}/labels", async (string slug, int id, SetTicketLabelsRequest req, TicketService ts) =>
        {
            var ok = await ts.SetTicketLabelsAsync(slug, id, req.LabelIds);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithTags("Labels");

        // Reorder
        api.MapPatch("/projects/{slug}/tickets/{id:int}/reorder", async (string slug, int id, ReorderTicketRequest req, TicketService ts) =>
        {
            await ts.ReorderTicketAsync(slug, id, req.Status, req.Index);
            return Results.NoContent();
        }).WithTags("Tickets");
    }
}
