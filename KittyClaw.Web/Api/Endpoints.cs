using System.Text;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;

namespace KittyClaw.Web.Api;

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
            var project = await ps.UpdateProjectAsync(slug, req.WorkspacePath, req.FallbackModel, req.UpdateFallbackModel);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).WithTags("Projects");

        api.MapPost("/projects/{slug}/pause", async (string slug, ProjectService ps) =>
        {
            var project = await ps.TogglePauseAsync(slug);
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
                if (ticket is not null && req.LabelIds is not null)
                    await ts.SetTicketLabelsAsync(slug, id, req.LabelIds);
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
        api.MapGet("/browse/capabilities", (KittyClaw.Core.Platform.IFolderPicker? picker) =>
            Results.Ok(new { folderPicker = picker?.IsAvailable == true }))
            .WithTags("Browse");

        api.MapPost("/browse/folder", async (BrowseFolderRequest? req, KittyClaw.Core.Platform.IFolderPicker? picker, CancellationToken ct) =>
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

        // Available skills for a project (scanned from WorkspacePath/.agents/<agent>/SKILL.md)
        api.MapGet("/projects/{slug}/skills", async (string slug, ProjectService ps) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();
            var dir = Path.Combine(ps.ResolveWorkspacePath(project), ".agents");
            if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<string>());
            var skills = Directory.EnumerateDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                .Select(d => Path.GetFileName(d)!)
                .OrderBy(s => s)
                .ToList();
            return Results.Ok(skills);
        }).WithTags("Automations");

        // Automations
        api.MapGet("/projects/{slug}/automations", async (string slug, AutomationStore store) =>
        {
            try
            {
                var (config, workspace, path) = await store.LoadAsync(slug);
                return Results.Ok(new { config, workspace, path });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
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

        api.MapGet("/projects/{slug}/runs/{runId}/stream", async (string slug, string runId, string? since, HttpContext http, AgentRunRegistry reg, CancellationToken ct) =>
        {
            var run = reg.Get(runId);
            if (run is null || run.ProjectSlug != slug) { http.Response.StatusCode = 404; return; }
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // Optional ?since=<ISO timestamp> filter: replay only buffer events strictly after that
            // instant. Used when a chat drawer reattaches mid-run and already has all events up to
            // its latest persisted message — without this, the buffered events would re-render as
            // duplicates.
            DateTime? sinceUtc = null;
            if (!string.IsNullOrWhiteSpace(since)
                && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                sinceUtc = parsed.ToUniversalTime();
            }

            var queue = System.Threading.Channels.Channel.CreateUnbounded<StreamEvent>();
            void handler(StreamEvent ev) => queue.Writer.TryWrite(ev);
            run.OnEvent += handler;

            try
            {
                foreach (var ev in run.SnapshotBuffer())
                {
                    if (sinceUtc is not null && ev.At <= sinceUtc.Value) continue;
                    await WriteSseAsync(http.Response, ev, ct);
                }

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

        // Owner chat (ad-hoc Claude session)
        api.MapGet("/projects/{slug}/chat/targets", async (string slug, ProjectService ps, MemberService ms, ChatService cs) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            var targets = new List<ChatTargetDto>
            {
                new("owner-chat", "Claude", "claude"),
            };
            var members = await ms.ListMembersAsync(slug);
            foreach (var m in members)
                targets.Add(new ChatTargetDto(m.Slug, m.Name, "member"));

            var lastTarget = await cs.LastTargetAsync(slug);
            return Results.Ok(new ChatTargetsResponse(lastTarget, targets));
        }).WithTags("Chat");

        api.MapGet("/projects/{slug}/chat/messages", async (string slug, string target, ChatService cs) =>
        {
            var rows = await cs.ListAsync(slug, target);
            var dtos = rows.Select(r => new ChatMessageDto(r.Role, r.Text, r.ToolName, r.Detail, r.CreatedAt)).ToList();
            return Results.Ok(dtos);
        }).WithTags("Chat");

        // Returns the runId of an in-flight chat run for (slug, target), or null.
        // Used by the drawer to reattach the SSE stream when reopened mid-run, so that
        // assistant turns emitted while the drawer was closed (and any subsequent ones)
        // surface in the UI.
        api.MapGet("/projects/{slug}/chat/active", (string slug, string target, AgentRunRegistry reg) =>
        {
            var group = $"chat:{slug}:{target}";
            var active = reg.ActiveForProject(slug)
                .FirstOrDefault(r => r.ConcurrencyGroup == group);
            return Results.Ok(new { runId = active?.RunId });
        }).WithTags("Chat");

        api.MapDelete("/projects/{slug}/chat/session", async (string slug, string target, ProjectService ps, ChatService cs, SessionRegistry sessions) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();
            var workspacePath = ps.ResolveWorkspacePath(project);
            await cs.ClearAsync(slug, target);
            sessions.Clear(workspacePath, $"chat:{target}", null);
            return Results.NoContent();
        }).WithTags("Chat");

        api.MapPost("/projects/{slug}/chat/start", async (string slug, ChatStartRequest req, ProjectService ps, MemberService ms, ChatService cs, TicketService ts, ClaudeRunner runner, SessionRegistry sessions, HttpContext http) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            var target = string.IsNullOrWhiteSpace(req.Target) ? "owner-chat" : req.Target;
            var runId = Guid.NewGuid().ToString("N");
            var workspacePath = ps.ResolveWorkspacePath(project);

            // A ticket-scoped chat target looks like "{agent}#ticket-{id}". The hash-suffix
            // namespaces ChatService rows so each ticket has its own thread with the agent.
            // We pass the parsed ticketId to ClaudeRunContext.TicketId so the underlying
            // claude session is also per-ticket (session key "chat:{agent}:{ticketId}").
            var (baseAgent, parsedTicketId) = ParseChatTarget(target);
            var effectiveTicketId = req.TicketId ?? parsedTicketId;

            if (req.ForceNew)
            {
                await cs.ClearAsync(slug, target);
                sessions.Clear(workspacePath, $"chat:{baseAgent}", effectiveTicketId);
            }

            await cs.AppendAsync(slug, target, "user", req.Message);

            // Build ticket-context block when this chat is scoped to a ticket.
            string? ticketContext = null;
            if (effectiveTicketId is int tid)
            {
                var ticket = await ts.GetTicketAsync(slug, tid);
                if (ticket is not null)
                {
                    var tb = new StringBuilder();
                    tb.AppendLine($"## Current ticket: #{ticket.Id} — {ticket.Title}");
                    tb.AppendLine();
                    tb.AppendLine($"- Status: `{ticket.Status}`");
                    tb.AppendLine($"- Priority: `{ticket.Priority}`");
                    if (!string.IsNullOrWhiteSpace(ticket.AssignedTo))
                        tb.AppendLine($"- Assigned to: `{ticket.AssignedTo}`");
                    if (ticket.ParentId is int pid)
                        tb.AppendLine($"- Parent ticket: #{pid}");
                    if (ticket.Labels.Count > 0)
                        tb.AppendLine($"- Labels: {string.Join(", ", ticket.Labels.Select(l => l.Name))}");
                    tb.AppendLine();
                    tb.AppendLine("### Description");
                    tb.AppendLine(string.IsNullOrWhiteSpace(ticket.Description) ? "_(empty)_" : ticket.Description);
                    if (ticket.Comments.Count > 0)
                    {
                        tb.AppendLine();
                        tb.AppendLine("### Comments");
                        foreach (var c in ticket.Comments.OrderBy(c => c.CreatedAt))
                            tb.AppendLine($"- **{c.Author}** ({c.CreatedAt:g}): {c.Content}");
                    }
                    if (ticket.SubTickets.Count > 0)
                    {
                        tb.AppendLine();
                        tb.AppendLine("### Sub-tickets");
                        foreach (var st in ticket.SubTickets)
                            tb.AppendLine($"- #{st.Id} [{st.Status}] {st.Title}");
                    }
                    ticketContext = tb.ToString();
                }
            }

            ClaudeRunContext ctx;
            if (target == "owner-chat")
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Context");
                sb.AppendLine();
                sb.AppendLine("You are an AI assistant embedded in the **KittyClaw** application — a Blazor Server kanban board that orchestrates agentic Claude workflows.");
                sb.AppendLine($"The owner is currently viewing the project **{project.Name}** (slug: `{slug}`).");
                sb.AppendLine($"Project workspace: `{workspacePath}`");
                sb.AppendLine();
                sb.AppendLine("Respond concisely and helpfully. You can read and modify files in the workspace, create tickets via the API, or give advice.");
                sb.AppendLine();

                var claudeMd = Path.Combine(workspacePath, "CLAUDE.md");
                if (File.Exists(claudeMd))
                {
                    sb.AppendLine("## CLAUDE.md");
                    sb.AppendLine();
                    sb.AppendLine(await File.ReadAllTextAsync(claudeMd));
                    sb.AppendLine();
                }

                var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
                sb.AppendLine("## KittyClaw App API");
                sb.AppendLine();
                sb.AppendLine($"Base URL: `{baseUrl}`");
                sb.AppendLine($"Current project slug: `{slug}`");
                sb.AppendLine();
                sb.AppendLine("Key endpoints:");
                sb.AppendLine($"- GET  {baseUrl}/api/projects/{slug}/tickets — list tickets");
                sb.AppendLine($"- POST {baseUrl}/api/projects/{slug}/tickets — create ticket (body: {{title, createdBy, status, description, priority}})");
                sb.AppendLine($"- GET  {baseUrl}/api/projects/{slug}/tickets/{{id}} — get ticket");
                sb.AppendLine($"- POST {baseUrl}/api/projects/{slug}/tickets/{{id}}/comments — add comment (body: {{content, author}})");
                sb.AppendLine($"- PATCH {baseUrl}/api/projects/{slug}/tickets/{{id}}/status — move ticket (body: {{status, author}})");
                sb.AppendLine($"- GET  {baseUrl}/api/projects/{slug}/columns — list columns");
                sb.AppendLine($"- Full API doc: {baseUrl}/api/docs");

                ctx = new ClaudeRunContext
                {
                    ProjectSlug = slug,
                    WorkspacePath = workspacePath,
                    AgentName = "owner-chat",
                    SkillFile = "chat",
                    InlineSkillContent = ticketContext is null ? sb.ToString() : sb.ToString() + "\n" + ticketContext,
                    ExtraContext = req.Message,
                    MaxTurns = 20,
                    ConcurrencyGroup = $"chat:{slug}:{target}",
                    PresetRunId = runId,
                    SessionScope = "chat",
                    TicketId = effectiveTicketId,
                    RetryOnResumeFailure = true,
                    OnEventHook = ev => PersistChatEvent(cs, slug, target, ev),
                };
            }
            else
            {
                var member = (await ms.ListMembersAsync(slug)).FirstOrDefault(m => m.Slug == baseAgent);
                var memberName = member?.Name ?? baseAgent;

                var skillPath = Path.Combine(workspacePath, ".agents", baseAgent, "SKILL.md");
                var hasSkillFile = File.Exists(skillPath);

                // Chat mode preamble overrides the automation-style instructions a SKILL.md
                // typically carries (e.g. "the brief lives in ticket comments"). In a live
                // chat the owner's request is in the user turn, not on the ticket — say so
                // explicitly so the agent doesn't go fishing for missing comments.
                var chatPreamble = new StringBuilder();
                chatPreamble.AppendLine("# Interactive chat mode");
                chatPreamble.AppendLine();
                chatPreamble.AppendLine($"You are **{memberName}**, talking live with the owner through an in-app chat — NOT running an automation.");
                chatPreamble.AppendLine();
                chatPreamble.AppendLine("Rules for this mode:");
                chatPreamble.AppendLine("- The owner's request is the **user message in this conversation**. Act on it directly.");
                chatPreamble.AppendLine("- Do NOT ask the owner to post their request as a ticket comment — they are speaking to you here.");
                chatPreamble.AppendLine("- Do NOT search ticket comments for instructions; treat the chat itself as the source of truth.");
                chatPreamble.AppendLine("- Respond conversationally and concisely. Use tools (Bash, Edit, etc.) when the owner asks you to perform an action.");
                if (ticketContext is not null)
                    chatPreamble.AppendLine($"- The current ticket below is the topic of this thread. Modify it via the API (PATCH `/api/projects/{slug}/tickets/{effectiveTicketId}`) or other tools when asked.");
                chatPreamble.AppendLine();

                // The chat-mode preamble applies to every chat session (ticket-scoped or not).
                // SKILL.md, when present, is appended after the preamble as background context
                // about the agent's specialty — not as operational instructions.
                var skillSection = "";
                if (hasSkillFile)
                {
                    var skillText = await File.ReadAllTextAsync(skillPath);
                    skillSection = "\n## Background — your specialty (from SKILL.md)\n\n" + skillText + "\n";
                }
                else
                {
                    skillSection = $"\nYou are {memberName}, an LLM member of project {project.Name}.\n";
                }
                var inlineContent = chatPreamble.ToString() + skillSection + (ticketContext is null ? "" : "\n" + ticketContext);

                ctx = new ClaudeRunContext
                {
                    ProjectSlug = slug,
                    WorkspacePath = workspacePath,
                    AgentName = baseAgent,
                    SkillFile = hasSkillFile ? $"{baseAgent}/SKILL.md" : "(inline)",
                    InlineSkillContent = inlineContent,
                    ExtraContext = req.Message,
                    MaxTurns = 20,
                    ConcurrencyGroup = $"chat:{slug}:{target}",
                    PresetRunId = runId,
                    SessionScope = "chat",
                    TicketId = effectiveTicketId,
                    RetryOnResumeFailure = true,
                    OnEventHook = ev => PersistChatEvent(cs, slug, target, ev),
                };
            }

            _ = runner.RunAsync(ctx, CancellationToken.None);
            return Results.Ok(new { runId });
        }).WithTags("Chat");

        // Images
        api.MapPost("/images", async (HttpRequest req, ProjectService ps) =>
        {
            if (!req.HasFormContentType || req.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file provided" });
            var file = req.Form.Files[0];
            if (!file.ContentType.StartsWith("image/"))
                return Results.BadRequest(new { error = "File must be an image" });
            var ext = file.ContentType.Split('/')[1].Split('+')[0];
            if (!AllowedImageExts.Contains(ext)) ext = "png";
            var filename = $"{Guid.NewGuid():N}.{ext}";
            var uploadsDir = Path.Combine(ps.DataDir, "uploads");
            Directory.CreateDirectory(uploadsDir);
            await using var fs = File.Create(Path.Combine(uploadsDir, filename));
            await file.CopyToAsync(fs);
            return Results.Ok(new { url = $"/uploads/{filename}" });
        }).WithTags("Images").DisableAntiforgery();

    }

    private static readonly JsonSerializerOptions SseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HashSet<string> AllowedImageExts = ["png", "jpeg", "jpg", "gif", "webp", "svg"];

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

    /// <summary>
    /// Parses a chat target slug. A bare slug like "programmer" or "owner-chat" is returned
    /// as (slug, null). A ticket-scoped target like "programmer#ticket-42" returns
    /// ("programmer", 42). Unknown suffix shapes are passed through as bare.
    /// </summary>
    private static (string BaseAgent, int? TicketId) ParseChatTarget(string target)
    {
        var hashIdx = target.IndexOf('#');
        if (hashIdx < 0) return (target, null);
        var head = target[..hashIdx];
        var tail = target[(hashIdx + 1)..];
        const string prefix = "ticket-";
        if (tail.StartsWith(prefix) && int.TryParse(tail.AsSpan(prefix.Length), out var id))
            return (head, id);
        return (target, null);
    }

    private static void PersistChatEvent(ChatService cs, string slug, string target, StreamEvent ev)
    {
        // Only persist what the drawer actually renders to the user.
        if (ev.Kind == "assistant")
        {
            const string prefix = "[assistant] ";
            var text = ev.Text.StartsWith(prefix) ? ev.Text[prefix.Length..] : ev.Text;
            text = text.Trim();
            if (string.IsNullOrEmpty(text) || text.StartsWith("tool:")) return;
            _ = cs.AppendAsync(slug, target, "assistant", text);
        }
        else if (ev.Kind == "tool_use")
        {
            _ = cs.AppendAsync(slug, target, "tool_use", ev.Text, toolName: ev.Text, detail: ev.Detail);
        }
        else if (ev.Kind == "reset")
        {
            _ = cs.AppendAsync(slug, target, "reset", ev.Text);
        }
    }
}

