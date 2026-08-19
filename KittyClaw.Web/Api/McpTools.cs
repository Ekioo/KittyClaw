using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KittyClaw.Web.Api;

/// <summary>
/// MCP tool surface (v1) exposed at /mcp — seven thin proxies over the same services the
/// REST API uses, so any MCP client (Claude Code, etc.) can drive the board. Deliberately
/// scoped: no runs, automations or dashboards until the channel proves itself (ticket #217).
/// Trust boundary is unchanged — the endpoint binds like the REST API (unauthenticated,
/// localhost, self-hosted single machine). Enable it from KittyClaw's global settings.
/// </summary>
[McpServerToolType]
public sealed class McpTools(
    ProjectService projects,
    TicketService tickets,
    ColumnService columns,
    MemberService members,
    BoardUpdateNotifier notifier)
{
    /// <summary>
    /// Mirrors the REST pipeline's JSON shape (camelCase + string enums) so agents that
    /// already know the REST API — or read /api/docs — see identical payloads over MCP.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // The SDK freezes the options at startup for schema generation; a fresh instance has
        // no resolver until first use, so the reflection resolver must be set explicitly.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
    };

    [McpServerTool(Name = "list_projects", ReadOnly = true, Idempotent = true)]
    [Description("List all KittyClaw projects (kanban boards) with their slugs. The slug identifies the project in every other tool.")]
    public Task<List<Project>> ListProjects() => projects.ListProjectsAsync();

    [McpServerTool(Name = "list_tickets", ReadOnly = true, Idempotent = true)]
    [Description("List the tickets of a project, optionally filtered by status (column name) and/or assignee (member slug). Returns summaries; use get_ticket for full detail.")]
    public async Task<List<TicketSummary>> ListTickets(
        [Description("Project slug (see list_projects).")] string project,
        [Description("Optional column/status filter, e.g. Backlog, Todo, InProgress, Review, Done.")] string? status = null,
        [Description("Optional assignee filter: a member slug (see board_overview).")] string? assignee = null)
    {
        await RequireProjectAsync(project);
        return await tickets.ListTicketsAsync(project, status, assignedTo: assignee);
    }

    [McpServerTool(Name = "get_ticket", ReadOnly = true, Idempotent = true)]
    [Description("Get one ticket in full: description, comments, activity log, labels, sub-tickets and dependencies.")]
    public async Task<Ticket> GetTicket(
        [Description("Project slug.")] string project,
        [Description("Ticket id.")] int id)
    {
        await RequireProjectAsync(project);
        var ticket = await tickets.GetTicketAsync(project, id);
        return ticket ?? throw new McpException($"Ticket #{id} was not found in project '{project}'.");
    }

    [McpServerTool(Name = "create_ticket")]
    [Description("Create a ticket on a project's board. Returns the created ticket, including its id.")]
    public async Task<Ticket> CreateTicket(
        [Description("Project slug.")] string project,
        [Description("Ticket title.")] string title,
        [Description("Ticket description (Markdown).")] string? description = null,
        [Description("Target column/status. Defaults to Backlog.")] string? status = null,
        [Description("Priority: Idea, NiceToHave, Required or Critical. Defaults to NiceToHave.")] TicketPriority? priority = null,
        [Description("Optional assignee: a member slug (see board_overview).")] string? assignee = null,
        [Description("Author recorded as the ticket creator. Defaults to 'owner'.")] string? author = null)
    {
        await RequireProjectAsync(project);
        try
        {
            var ticket = await tickets.CreateTicketAsync(
                project, title, description ?? "", author ?? "owner",
                status ?? "Backlog", priority: priority ?? TicketPriority.NiceToHave,
                assignedTo: assignee);
            notifier.NotifyProjectUpdated(project);
            return ticket;
        }
        catch (TicketCreationSaturationException ex)
        {
            throw new McpException(
                $"{ex.Message} (blocked: {ex.BlockedCount}/{ex.BlockedLimit} — resolve blocked tickets before creating new ones)");
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "comment_ticket")]
    [Description("Add a comment to a ticket. Returns the created comment.")]
    public async Task<Comment> CommentTicket(
        [Description("Project slug.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Comment body (Markdown).")] string content,
        [Description("Author recorded on the comment. Defaults to 'owner'.")] string? author = null)
    {
        await RequireProjectAsync(project);
        try
        {
            var comment = await tickets.AddCommentAsync(project, id, content, author ?? "owner");
            if (comment is null)
                throw new McpException($"Ticket #{id} was not found in project '{project}'.");
            notifier.NotifyProjectUpdated(project);
            return comment;
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "move_ticket")]
    [Description("Move a ticket to another column/status (e.g. Todo, InProgress, Review, Done). Returns the updated ticket.")]
    public async Task<Ticket> MoveTicket(
        [Description("Project slug.")] string project,
        [Description("Ticket id.")] int id,
        [Description("Target column/status name.")] string status,
        [Description("Author recorded on the move. Defaults to 'owner'.")] string? author = null)
    {
        await RequireProjectAsync(project);
        try
        {
            var ticket = await tickets.UpdateTicketAsync(
                project, id, author: author ?? "owner", status: status, enforceRouting: true);
            if (ticket is null)
                throw new McpException($"Ticket #{id} was not found in project '{project}'.");
            notifier.NotifyProjectUpdated(project);
            return ticket;
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "board_overview", ReadOnly = true, Idempotent = true)]
    [Description("Overview of a project's board: its columns (statuses, in order) and its members (valid assignees/authors).")]
    public async Task<BoardOverview> GetBoardOverview(
        [Description("Project slug.")] string project)
    {
        var proj = await RequireProjectAsync(project);
        return new BoardOverview(
            proj.Slug, proj.Name,
            await columns.ListColumnsAsync(project),
            await members.ListMembersAsync(project));
    }

    public sealed record BoardOverview(string Slug, string Name, List<BoardColumn> Columns, List<Member> Members);

    /// <summary>
    /// The REST endpoints tolerate unknown slugs (GetProjectDb silently creates an empty db);
    /// an MCP agent guessing a slug deserves a clear error instead of a phantom empty board.
    /// </summary>
    private async Task<Project> RequireProjectAsync(string slug)
    {
        return await projects.GetProjectAsync(slug) ?? throw new McpException(
            $"Unknown project '{slug}'. Use list_projects to discover valid slugs.");
    }
}
