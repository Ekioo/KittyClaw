using Todo.Core.Models;
using Todo.Core.Services;
using Todo.Web.Services;

namespace Todo.Web.Api;

public static class Endpoints
{
    public static void MapTodoApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Columns (per-project)
        api.MapGet("/projects/{slug}/columns", async (string slug, ColumnService cs) =>
            Results.Ok(await cs.ListColumnsAsync(slug)))
            .WithTags("Columns");

        api.MapPost("/projects/{slug}/columns", async (string slug, CreateColumnRequest req, ColumnService cs, BoardUpdateNotifier notifier) =>
        {
            var column = await cs.CreateColumnAsync(slug, req.Name, req.Color);
            notifier.NotifyProjectUpdated(slug);
            return Results.Created($"/api/projects/{slug}/columns/{column.Id}", column);
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
        api.MapGet("/projects/{slug}/tickets", async (string slug, string? status, TicketPriority? priority, string? assignedTo, string? createdBy, string? search, TicketService ts) =>
            Results.Ok(await ts.ListTicketsAsync(slug, status, priority, assignedTo, createdBy, search)))
            .WithTags("Tickets");

        api.MapPost("/projects/{slug}/tickets", async (string slug, CreateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.CreateTicketAsync(slug, req.Title, req.Description, req.CreatedBy, req.Status, req.LabelIds, req.Priority, req.AssignedTo);
                notifier.NotifyProjectUpdated(slug);
                return Results.Created($"/api/projects/{slug}/tickets/{ticket.Id}", ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets");

        api.MapPatch("/projects/{slug}/tickets/{id:int}", async (string slug, int id, UpdateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.UpdateTicketAsync(slug, id, req.Title, req.Description, req.Author, req.Priority, req.AssignedTo);
                if (ticket is not null) notifier.NotifyProjectUpdated(slug);
                return ticket is null ? Results.NotFound() : Results.Ok(ticket);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithTags("Tickets");

        api.MapGet("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts) =>
        {
            var ticket = await ts.GetTicketAsync(slug, id);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).WithTags("Tickets");

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
        }).WithTags("Tickets");

        api.MapDelete("/projects/{slug}/tickets/{id:int}", async (string slug, int id, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            var deleted = await ts.DeleteTicketAsync(slug, id);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Tickets");

        // Comments
        api.MapPost("/projects/{slug}/tickets/{id:int}/comments", async (string slug, int id, AddCommentRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var comment = await ts.AddCommentAsync(slug, id, req.Content, req.Author);
                if (comment is not null) notifier.NotifyProjectUpdated(slug);
                return comment is null ? Results.NotFound() : Results.Created($"/api/projects/{slug}/tickets/{id}", comment);
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
                var ok = await ts.UpdateCommentAsync(slug, id, commentId, req.Content, req.Author);
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

        // Labels (project-level)
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

        // Labels (ticket-level)
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

        // Reorder
        api.MapPatch("/projects/{slug}/tickets/{id:int}/reorder", async (string slug, int id, ReorderTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            await ts.ReorderTicketAsync(slug, id, req.Status, req.Index);
            notifier.NotifyProjectUpdated(slug);
            return Results.NoContent();
        }).WithTags("Tickets");

        // Members (project-level)
        api.MapGet("/projects/{slug}/members", async (string slug, MemberService ms) =>
            Results.Ok(await ms.ListMembersAsync(slug)))
            .WithTags("Members");

        api.MapPost("/projects/{slug}/members", async (string slug, CreateMemberRequest req, MemberService ms, BoardUpdateNotifier notifier) =>
        {
            var member = await ms.CreateMemberAsync(slug, req.Name);
            notifier.NotifyProjectUpdated(slug);
            return Results.Created($"/api/projects/{slug}/members/{member.Id}", member);
        }).WithTags("Members");

        api.MapPatch("/projects/{slug}/members/{memberId:int}", async (string slug, int memberId, UpdateMemberRequest req, MemberService ms, BoardUpdateNotifier notifier) =>
        {
            var member = await ms.UpdateMemberAsync(slug, memberId, req.Name);
            if (member is not null) notifier.NotifyProjectUpdated(slug);
            return member is null ? Results.NotFound() : Results.Ok(member);
        }).WithTags("Members");

        api.MapDelete("/projects/{slug}/members/{memberId:int}", async (string slug, int memberId, MemberService ms, BoardUpdateNotifier notifier) =>
        {
            var deleted = await ms.DeleteMemberAsync(slug, memberId);
            if (deleted) notifier.NotifyProjectUpdated(slug);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithTags("Members");

        // Mentions
        api.MapGet("/projects/{slug}/mentions/{handle}", async (string slug, string handle, DateTime? since, DateTime? until, TicketService ts) =>
        {
            var tickets = await ts.ListMentionedTicketsAsync(slug, handle, since, until);
            return Results.Ok(tickets);
        }).WithTags("Mentions");

        // Images
        api.MapPost("/images", async (HttpRequest req, ProjectService ps) =>
        {
            if (!req.HasFormContentType || req.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file provided" });
            var file = req.Form.Files[0];
            if (!file.ContentType.StartsWith("image/"))
                return Results.BadRequest(new { error = "File must be an image" });
            var ext = file.ContentType.Split('/')[1].Split('+')[0];
            var allowed = new HashSet<string> { "png", "jpeg", "jpg", "gif", "webp", "svg" };
            if (!allowed.Contains(ext)) ext = "png";
            var filename = $"{Guid.NewGuid():N}.{ext}";
            var uploadsDir = Path.Combine(ps.DataDir, "uploads");
            Directory.CreateDirectory(uploadsDir);
            await using var fs = File.Create(Path.Combine(uploadsDir, filename));
            await file.CopyToAsync(fs);
            return Results.Ok(new { url = $"/uploads/{filename}" });
        }).WithTags("Images").DisableAntiforgery();
    }
}
