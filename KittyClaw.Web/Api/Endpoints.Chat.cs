using System.Text;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapChat(RouteGroupBuilder api)
    {
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

        api.MapPost("/projects/{slug}/chat/start", async (string slug, ChatStartRequest req, ProjectService ps, MemberService ms, ChatService cs, TicketService ts, ClaudeRunner runner, SessionRegistry sessions, AgentRunRegistry runReg, HttpContext http) =>
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

            // Carry over undelivered steer messages from the most recent completed run for this chat target.
            var chatGroup = $"chat:{slug}:{target}";
            var lastCompletedRun = runReg.AllForProject(slug)
                .Where(r => r.ConcurrencyGroup == chatGroup && r.Status == AgentRunStatus.Completed && r.PendingSteerMessages.Count > 0)
                .OrderByDescending(r => r.EndedAt)
                .FirstOrDefault();
            IReadOnlyList<string>? pendingSteerMessages = lastCompletedRun?.PendingSteerMessages;

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
                    ChatTarget = target,
                    PendingSteerMessages = pendingSteerMessages,
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
                    ChatTarget = target,
                    PendingSteerMessages = pendingSteerMessages,
                };
            }

            _ = runner.RunAsync(ctx, CancellationToken.None);
            return Results.Ok(new { runId });
        }).WithTags("Chat");
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
        // "inject" events are persisted directly by the steer endpoint — skip here to avoid double-write.
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
