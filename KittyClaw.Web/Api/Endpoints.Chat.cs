using System.Text;
using System.Collections.Concurrent;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static readonly ConcurrentDictionary<string, byte> QaFailNextChatStarts = new(StringComparer.Ordinal);
    private static void MapChat(RouteGroupBuilder api)
    {
        // Owner chat (ad-hoc Claude session)
        api.MapGet("/projects/{slug}/chat/targets", async (string slug, ProjectService ps, MemberService ms, ChatService cs) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            var targets = new List<ChatTargetDto>
            {
                new("owner-chat", "KittyClaw", "claude"),
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
            var dtos = rows.Select(r => new ChatMessageDto(
                r.Role, r.Text, r.ToolName, r.Detail, r.CreatedAt,
                ChatService.DeserializeImages(r.ImagesJson)
                    .Select(i => new ChatImageDto(i.DataUrl, i.Mime, i.Name, i.SizeBytes))
                    .ToList())).ToList();
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
            var interrupted = active is null
                && reg.LastInterruptedForChatTarget(slug, target) is not null;
            return Results.Ok(new { runId = active?.RunId, interrupted });
        }).WithTags("Chat");

        api.MapGet("/projects/{slug}/chat/model", async (
            string slug, string target, ProjectService ps, ChatService cs,
            SessionRegistry sessions, AgentRunRegistry runs) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();
            var workspacePath = ps.ResolveWorkspacePath(project);
            var model = sessions.GetLastChatModel(workspacePath, target);
            if (model is null && await cs.AnyAsync(slug, target))
                model = runs.LastCompletedForChatTarget(slug, target)?.Model;
            return Results.Ok(new { model });
        }).WithTags("Chat");

        api.MapDelete("/projects/{slug}/chat/session", async (string slug, string target, ProjectService ps, ChatService cs, SessionRegistry sessions) =>
        {
            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();
            var workspacePath = ps.ResolveWorkspacePath(project);
            await cs.ClearAsync(slug, target);
            sessions.Clear(workspacePath, $"chat:{target}", null);
            sessions.Clear(workspacePath, $"grok:chat:{target}", null);
            sessions.Clear(workspacePath, $"codex:chat:{target}", null);
            sessions.Clear(workspacePath, $"mistral:chat:{target}", null);
            sessions.ClearLastChatModel(workspacePath, target);
            return Results.NoContent();
        }).WithTags("Chat");

        api.MapPost("/projects/{slug}/chat/start", async (string slug, ChatStartRequest req, ProjectService ps, MemberService ms, ChatService cs, TicketService ts, AgentRunner runner, SessionRegistry sessions, AgentRunRegistry runReg, HttpContext http) =>
        {
            if (string.Equals(Environment.GetEnvironmentVariable("KITTYCLAW_ENABLE_QA_ENDPOINTS"), "1", StringComparison.Ordinal)
                && QaFailNextChatStarts.TryRemove(slug, out _))
                return Results.Json(new { error = "qa_forced_chat_start_failure" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var project = await ps.GetProjectAsync(slug);
            if (project is null) return Results.NotFound();

            var target = string.IsNullOrWhiteSpace(req.Target) ? "owner-chat" : req.Target;
            if (req.ResumeInterrupted)
            {
                var active = runReg.ActiveForProject(slug)
                    .FirstOrDefault(r => r.ConcurrencyGroup == $"chat:{slug}:{target}");
                if (active is not null)
                    return Results.Ok(new { runId = active.RunId });
                if (runReg.LastInterruptedForChatTarget(slug, target) is null)
                    return Results.Conflict(new { error = "chat_not_interrupted" });
            }
            var workspacePath = ps.ResolveWorkspacePath(project);
            var chatHistory = req.ForceNew
                ? new List<KittyClaw.Core.Models.ChatMessageRow>()
                : await cs.ListAsync(slug, target);
            var storedConversationModel = req.ForceNew
                ? null
                : sessions.GetLastChatModel(workspacePath, target);
            var legacyConversationModel = chatHistory.Count > 0
                ? runReg.LastCompletedForChatTarget(slug, target)?.Model
                : null;
            var requestedModel = storedConversationModel ?? legacyConversationModel ?? req.Model;

            // Resolve which CLI runs this chat turn (claude, claude+Ollama env, or grok).
            string? effectiveModel = null;
            Dictionary<string, string>? modelEnv = null;
            var provider = CliProvider.Claude;
            string? modelValidationError = null;
            if (!string.IsNullOrEmpty(requestedModel))
            {
                var routing = ModelRouting.Resolve(requestedModel, project.LocalModelBaseUrl);
                if (routing.Error is null)
                {
                    effectiveModel = routing.ResolvedModel ?? requestedModel;
                    provider = routing.Provider;
                    modelEnv = routing.ExtraEnv is null ? null : new Dictionary<string, string>(routing.ExtraEnv);
                }
                else if (GrokCli.IsGrokModel(requestedModel) || CodexCli.IsCodexModel(requestedModel)
                         || MistralCli.IsMistralModel(requestedModel))
                {
                    // Surface a missing native CLI in the chat stream rather than silently
                    // answering with the default Claude model.
                    effectiveModel = requestedModel;
                    modelValidationError = routing.Error;
                }
                // Ollama model without a configured base URL: historical chat behavior —
                // model stays null and the turn runs on the CLI default.
            }
            var dispatchTarget = new AgentDispatchTarget(
                effectiveModel, provider, modelEnv ?? new Dictionary<string, string>(), modelValidationError);

            var runId = Guid.NewGuid().ToString("N");

            // A ticket-scoped chat target looks like "{agent}#ticket-{id}". The hash-suffix
            // namespaces ChatService rows so each ticket has its own thread with the agent.
            // We pass the parsed ticketId to AgentRunContext.TicketId so the underlying
            // claude session is also per-ticket (session key "chat:{agent}:{ticketId}").
            var (baseAgent, parsedTicketId) = ParseChatTarget(target);
            var effectiveTicketId = req.TicketId ?? parsedTicketId;

            // baseAgent is composed into filesystem paths (.agents/{baseAgent}/SKILL.md) and
            // session keys — reject anything that isn't a plain slug before touching disk.
            if (!baseAgent.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
                return Results.BadRequest(new { error = "invalid_target", reason = $"Invalid agent slug '{baseAgent}'." });

            // Drain undelivered steer messages from the most recent completed run for this chat target.
            // Drain (not read) so they are not replayed on subsequent turns.
            var pendingSteerMessages = runReg.LastCompletedForChatTarget(slug, target)?.DrainPendingSteerMessages();

            if (req.ForceNew)
            {
                await cs.ClearAsync(slug, target);
                sessions.Clear(workspacePath, $"chat:{baseAgent}", effectiveTicketId);
                sessions.Clear(workspacePath, $"grok:chat:{baseAgent}", effectiveTicketId);
                sessions.Clear(workspacePath, $"codex:chat:{baseAgent}", effectiveTicketId);
                sessions.Clear(workspacePath, $"mistral:chat:{baseAgent}", effectiveTicketId);
                sessions.ClearLastChatProvider(workspacePath, target);
                sessions.ClearLastChatModel(workspacePath, target);
            }

            var providerName = dispatchTarget.Provider.ToString();
            var previousProvider = sessions.GetLastChatProvider(workspacePath, target);
            var scopedAgent = AgentRunner.SessionScopeKey(
                baseAgent, "chat", dispatchTarget.Provider);
            var selectedProviderHasSession =
                sessions.GetSessionId(workspacePath, scopedAgent, effectiveTicketId) is not null;
            var providerChanged = previousProvider is not null &&
                !string.Equals(previousProvider, providerName, StringComparison.OrdinalIgnoreCase);
            var needsHandoff = chatHistory.Count > 0 &&
                (providerChanged || previousProvider is null || !selectedProviderHasSession);
            var conversationHandoff = needsHandoff
                ? ConversationHandoffBuilder.Build(chatHistory, previousProvider, dispatchTarget.Provider)
                : null;

            // Image paste validation (#115). Enforce MIME allow-list, per-image size cap,
            // and per-turn count cap server-side regardless of what the JS sent.
            var (imagePaths, imageError) = await PersistChatImagesAsync(req.Images, workspacePath, runId);
            if (imageError is not null)
                return Results.BadRequest(new { error = "image_rejected", reason = imageError });

            if (!req.ResumeInterrupted)
                await cs.AppendAsync(slug, target, "user", req.Message, images: req.Images?
                    .Select(i => new ChatMessageImage(i.DataUrl, i.Mime, i.Name, i.SizeBytes))
                    .ToList());
            sessions.SetLastChatProvider(workspacePath, target, providerName);
            if (!string.IsNullOrWhiteSpace(requestedModel))
                sessions.SetLastChatModel(workspacePath, target, requestedModel);

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

            var turnPrompt = req.ResumeInterrupted
                ? "KittyClaw restarted while the previous task was still running. Resume the interrupted task from the existing session and continue from where you stopped. Do not repeat work that is already complete."
                : req.Message;

            AgentRunContext ctx;
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

                ctx = new AgentRunContext
                {
                    ProjectSlug = slug,
                    WorkspacePath = workspacePath,
                    AgentName = "owner-chat",
                    SkillFile = "chat",
                    InlineSkillContent = ticketContext is null ? sb.ToString() : sb.ToString() + "\n" + ticketContext,
                    ExtraContext = turnPrompt,
                    Target = dispatchTarget,
                    MaxTurns = 20,
                    ConcurrencyGroup = $"chat:{slug}:{target}",
                    PresetRunId = runId,
                    SessionScope = "chat",
                    TicketId = effectiveTicketId,
                    RetryOnResumeFailure = true,
                    OnEventHook = ev => PersistChatEvent(cs, slug, target, ev),
                    ChatTarget = target,
                    PendingSteerMessages = pendingSteerMessages,
                    ConversationHandoff = conversationHandoff,
                    ImagePaths = imagePaths,
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

                ctx = new AgentRunContext
                {
                    ProjectSlug = slug,
                    WorkspacePath = workspacePath,
                    AgentName = baseAgent,
                    SkillFile = hasSkillFile ? $"{baseAgent}/SKILL.md" : "(inline)",
                    InlineSkillContent = inlineContent,
                    ExtraContext = turnPrompt,
                    Target = dispatchTarget,
                    MaxTurns = 20,
                    ConcurrencyGroup = $"chat:{slug}:{target}",
                    PresetRunId = runId,
                    SessionScope = "chat",
                    TicketId = effectiveTicketId,
                    RetryOnResumeFailure = true,
                    OnEventHook = ev => PersistChatEvent(cs, slug, target, ev),
                    ChatTarget = target,
                    PendingSteerMessages = pendingSteerMessages,
                    ConversationHandoff = conversationHandoff,
                    ImagePaths = imagePaths,
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

    private const long ChatImageMaxBytes = 5 * 1024 * 1024;
    private const int ChatImageMaxCount = 5;
    private static readonly HashSet<string> ChatImageAllowedMime = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp",
    };

    /// <summary>
    /// Validates and persists pasted images to <c>&lt;workspace&gt;/.agents/channel/tmp/</c>.
    /// Returns the list of absolute paths to forward to <see cref="AgentRunContext.ImagePaths"/>,
    /// or a non-null reason string for a 400 "image_rejected" response.
    /// </summary>
    private static async Task<(IReadOnlyList<string>? Paths, string? RejectReason)> PersistChatImagesAsync(
        IReadOnlyList<ChatImageDto>? images, string workspacePath, string runId)
    {
        if (images is null || images.Count == 0) return (null, null);
        if (images.Count > ChatImageMaxCount)
            return (null, $"too many images (max {ChatImageMaxCount})");

        var tmpDir = Path.Combine(workspacePath, ".agents", "channel", "tmp");
        Directory.CreateDirectory(tmpDir);

        var paths = new List<string>(images.Count);
        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            if (string.IsNullOrWhiteSpace(img.Mime) || !ChatImageAllowedMime.Contains(img.Mime))
                return (null, $"unsupported MIME type: {img.Mime}");
            if (img.SizeBytes > ChatImageMaxBytes)
                return (null, $"image too large (max {ChatImageMaxBytes} bytes)");
            if (string.IsNullOrWhiteSpace(img.DataUrl))
                return (null, "empty data URL");

            // data:image/png;base64,XXXX  →  XXXX
            var commaIdx = img.DataUrl.IndexOf(',');
            var base64 = commaIdx > 0 ? img.DataUrl[(commaIdx + 1)..] : img.DataUrl;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch { return (null, "malformed base64 payload"); }
            if (bytes.LongLength > ChatImageMaxBytes)
                return (null, $"image too large (max {ChatImageMaxBytes} bytes)");

            var ext = img.Mime switch
            {
                "image/jpeg" => "jpg",
                "image/png" => "png",
                "image/gif" => "gif",
                "image/webp" => "webp",
                _ => "bin",
            };
            var path = Path.Combine(tmpDir, $"chat-{runId}-{i}.{ext}");
            await File.WriteAllBytesAsync(path, bytes);
            paths.Add(path);
        }
        return (paths, null);
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
        else if (ev.Kind == "ask_user_question")
        {
            _ = cs.AppendAsync(slug, target, "ask_user_question", ev.Text ?? "", toolName: ev.Text, detail: ev.Detail);
        }
        else if (ev.Kind == "reset")
        {
            _ = cs.AppendAsync(slug, target, "reset", ev.Text);
        }
    }
}
