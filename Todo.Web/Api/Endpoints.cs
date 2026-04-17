using System.Text;
using System.Text.Json;
using Todo.Core.Automation;
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

        api.MapPatch("/projects/{slug}", async (string slug, UpdateProjectRequest req, ProjectService ps) =>
        {
            var project = await ps.UpdateProjectAsync(slug, req.WorkspacePath);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        // Tickets
        api.MapGet("/projects/{slug}/tickets", async (string slug, string? status, TicketPriority? priority, string? assignedTo, string? createdBy, string? search, int? parentId, TicketService ts) =>
            Results.Ok(await ts.ListTicketsAsync(slug, status, priority, assignedTo, createdBy, search, parentId)))
            .WithTags("Tickets");

        api.MapPost("/projects/{slug}/tickets", async (string slug, CreateTicketRequest req, TicketService ts, BoardUpdateNotifier notifier) =>
        {
            try
            {
                var ticket = await ts.CreateTicketAsync(slug, req.Title, req.Description, req.CreatedBy, req.Status, req.LabelIds, req.Priority, req.AssignedTo, req.ParentId);
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

        // Capability probe — lets the UI hide the browse button when no picker is available
        // (e.g. cloud-hosted deployment where the server has no desktop).
        api.MapGet("/browse/capabilities", (Todo.Core.Platform.IFolderPicker? picker) =>
            Results.Ok(new { folderPicker = picker?.IsAvailable == true }))
            .WithTags("Browse");

        api.MapPost("/browse/folder", async (BrowseFolderRequest? req, Todo.Core.Platform.IFolderPicker? picker, CancellationToken ct) =>
        {
            if (picker is null || !picker.IsAvailable)
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            try
            {
                var path = await picker.PickFolderAsync(req?.InitialPath, ct);
                return string.IsNullOrEmpty(path)
                    ? Results.NoContent()
                    : Results.Ok(new { path });
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        }).WithTags("Browse");

        // Available skills for a project (scanned from WorkspacePath/.agents/skills/*.md)
        api.MapGet("/projects/{slug}/skills", async (string slug, ProjectService ps) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();
            var dir = Path.Combine(ps.ResolveWorkspacePath(project), ".agents", "skills");
            if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<string>());
            var skills = Directory.EnumerateFiles(dir, "*.md")
                .Select(p => Path.GetFileNameWithoutExtension(p)!)
                .OrderBy(s => s)
                .ToList();
            return Results.Ok(skills);
        }).WithTags("Automations");

        // Automations
        api.MapGet("/projects/{slug}/automations", async (string slug, AutomationStore store) =>
        {
            var (config, workspace, path) = await store.LoadAsync(slug);
            return Results.Ok(new { config, workspace, path });
        }).WithTags("Automations");

        api.MapPut("/projects/{slug}/automations", async (string slug, AutomationConfig config, AutomationStore store, AutomationEngine engine) =>
        {
            await store.SaveAsync(slug, config);
            await engine.ReloadProjectAsync(slug);
            return Results.NoContent();
        }).WithTags("Automations");

        api.MapPost("/projects/{slug}/automations/reload", async (string slug, AutomationEngine engine) =>
        {
            await engine.ReloadProjectAsync(slug);
            return Results.NoContent();
        }).WithTags("Automations");

        api.MapPost("/projects/{slug}/automations/{id}/run", async (string slug, string id, AutomationEngine engine, CancellationToken ct) =>
        {
            try
            {
                var run = await engine.RunAutomationManuallyAsync(slug, id, ct);
                return Results.Accepted($"/api/projects/{slug}/runs/{run.RunId}", new { runId = run.RunId });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).WithTags("Automations");

        // Agent runs (live)
        api.MapGet("/projects/{slug}/runs", (string slug, AgentRunRegistry reg) =>
            Results.Ok(reg.ActiveForProject(slug).Select(r => new
            {
                r.RunId, r.AgentName, r.SkillFile, r.TicketId, r.ConcurrencyGroup,
                r.StartedAt, r.SessionId, status = r.Status.ToString(),
            })))
            .WithTags("Runs");

        api.MapGet("/projects/{slug}/runs/{runId}", (string slug, string runId, AgentRunRegistry reg) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            return Results.Ok(new
            {
                run.RunId, run.AgentName, run.SkillFile, run.TicketId, run.ConcurrencyGroup,
                run.StartedAt, run.EndedAt, run.SessionId, run.ExitCode,
                status = run.Status.ToString(),
                events = run.SnapshotBuffer(),
            });
        }).WithTags("Runs");

        api.MapGet("/projects/{slug}/runs/{runId}/stream", async (string slug, string runId, HttpContext http, AgentRunRegistry reg, CancellationToken ct) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) { http.Response.StatusCode = 404; return; }
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var queue = System.Threading.Channels.Channel.CreateUnbounded<StreamEvent>();
            void handler(StreamEvent ev) => queue.Writer.TryWrite(ev);
            run.OnEvent += handler;

            try
            {
                foreach (var ev in run.SnapshotBuffer())
                    await WriteSseAsync(http.Response, ev, ct);

                while (!ct.IsCancellationRequested && run.Status == AgentRunStatus.Running)
                {
                    while (queue.Reader.TryRead(out var ev))
                        await WriteSseAsync(http.Response, ev, ct);
                    try { await Task.Delay(200, ct); } catch { break; }
                }
                while (queue.Reader.TryRead(out var ev))
                    await WriteSseAsync(http.Response, ev, ct);
                await WriteSseRawAsync(http.Response, "event: end\ndata: {}\n\n", ct);
            }
            finally { run.OnEvent -= handler; }
        }).WithTags("Runs");

        api.MapPost("/projects/{slug}/runs/{runId}/steer", async (string slug, string runId, SteerRunRequest req, AgentRunRegistry reg) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            if (run.Status != AgentRunStatus.Running) return Results.BadRequest(new { error = "Run is not active." });
            await run.SteeringQueue.Writer.WriteAsync(req.Text);
            return Results.NoContent();
        }).WithTags("Runs");

        api.MapPost("/projects/{slug}/runs/{runId}/stop", (string slug, string runId, AgentRunRegistry reg) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) return Results.NotFound();
            run.Cancellation.Cancel();
            return Results.NoContent();
        }).WithTags("Runs");

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

    private static readonly JsonSerializerOptions SseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task WriteSseAsync(HttpResponse res, StreamEvent ev, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(ev, SseJson);
        await WriteSseRawAsync(res, $"data: {payload}\n\n", ct);
    }

    private static async Task WriteSseRawAsync(HttpResponse res, string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await res.Body.WriteAsync(bytes, ct);
        await res.Body.FlushAsync(ct);
    }
}
