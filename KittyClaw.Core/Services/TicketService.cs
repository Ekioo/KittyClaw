using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public class TicketService
{
    private readonly ProjectService _projectService;
    private readonly MemberService _memberService;
    private readonly ColumnProcessorService? _columnProcessors;
    private readonly SemaphoreSlim _scheduledPromotionLock = new(1, 1);
    private readonly AutomationStore? _automationStore;
    private readonly ILogger<TicketService>? _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _creationGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised after a ticket's status has been persisted.
    /// Parameters: (projectSlug, ticketId, fromStatus, toStatus)
    /// </summary>
    public event Action<string, int, string, string>? TicketStatusChanged;
    public event Action<string, int>? TicketCreated;

    internal void NotifyStatusChanged(string projectSlug, int ticketId, string fromStatus, string toStatus) =>
        TicketStatusChanged?.Invoke(projectSlug, ticketId, fromStatus, toStatus);

    /// <summary>
    /// Raised immediately after a comment is persisted.
    /// Parameters: (projectSlug, ticketId, commentId, author, content)
    /// </summary>
    public event Action<string, int, int, string, string>? TicketCommentAdded;

    public TicketService(
        ProjectService projectService,
        MemberService memberService,
        ColumnProcessorService? columnProcessors = null,
        AutomationStore? automationStore = null,
        ILogger<TicketService>? logger = null)
    {
        _projectService = projectService;
        _memberService = memberService;
        _columnProcessors = columnProcessors;
        _automationStore = automationStore;
        _logger = logger;
    }

    /// <summary>
    /// Ensures board columns exist, then returns the canonical column name for
    /// <paramref name="status"/> (case-insensitive match). Throws when the column is missing
    /// so tickets are never parked in a status the board cannot render.
    /// </summary>
    private static async Task<BoardColumn> RequireColumnAsync(
        TodoDbContext db, string? status, int? pipelineId = null, int? columnId = null)
    {
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        pipelineId ??= await db.Pipelines.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
        if (columnId is not null)
        {
            var byId = await db.BoardColumns.FirstOrDefaultAsync(c => c.Id == columnId && c.PipelineId == pipelineId);
            if (byId is null)
                throw new InvalidOperationException($"La colonne #{columnId} n'existe pas dans le pipeline #{pipelineId}.");
            return byId;
        }
        if (string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException("Le statut (colonne) est requis.");
        var requested = status.Trim();
        var columns = await db.BoardColumns.Where(c => c.PipelineId == pipelineId).ToListAsync();
        var match = columns.FirstOrDefault(c => string.Equals(c.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new InvalidOperationException($"La colonne '{requested}' n'existe pas dans le pipeline #{pipelineId}.");
        return match;
    }

    private static async Task<string> RequireColumnNameAsync(TodoDbContext db, string? status, int? pipelineId = null) =>
        (await RequireColumnAsync(db, status, pipelineId)).Name;

    private static bool IsScheduledColumnName(string? name) =>
        string.Equals(name, "Scheduled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Planifié", StringComparison.OrdinalIgnoreCase);

    private static async Task<BoardColumn> RequireScheduledColumnAsync(TodoDbContext db, int pipelineId)
    {
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var columns = await db.BoardColumns.Where(c => c.PipelineId == pipelineId).ToListAsync();
        return columns.FirstOrDefault(c => string.Equals(c.Name, "Scheduled", StringComparison.OrdinalIgnoreCase))
            ?? columns.FirstOrDefault(c => string.Equals(c.Name, "Planifié", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Aucune colonne de planification n'existe dans le pipeline #{pipelineId}.");
    }

    // Ensures the ActivityEntries table exists (for databases created before this feature)
    internal static Task EnsureActivityTableAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "activity-table", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ActivityEntries (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    TicketId INTEGER NOT NULL,
                    Author TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                )
            """);
            await d.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_ActivityEntries_TicketId ON ActivityEntries(TicketId)");
        });

    private static Task EnsureLabelTablesAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "label-tables", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS Labels (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Color TEXT NOT NULL DEFAULT '#6366f1'
                )
            """);
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TicketLabels (
                    TicketsId INTEGER NOT NULL,
                    LabelsId INTEGER NOT NULL,
                    PRIMARY KEY (TicketsId, LabelsId)
                )
            """);
        });

    private static Task EnsureSortOrderColumnAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-sortorder", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0"));

    private static Task EnsureAssignedToColumnAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-assignedto", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AssignedTo TEXT NULL"));

    private static Task EnsureParentIdColumnAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-parentid", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN ParentId INTEGER NULL"));

    // Adds the Scheduled-status columns (feature #99) to databases created before this feature.
    private static Task EnsureScheduleColumnsAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-schedule", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN FireAt TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN ScheduleTarget TEXT NULL");
        });

    // Adds the cumulative agent token-usage columns to databases created before this feature.
    internal static Task EnsureAgentUsageColumnsAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-agent-usage-estimate", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AgentTokens INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AgentCostUsd REAL NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AgentCostEstimated INTEGER NOT NULL DEFAULT 0");
        });

    // Hot-path indexes: status/parent filters run on every board render, and the activity
    // subquery in ListTicketsAsync scans per ticket. Must run after the column migrations.
    private static Task EnsureTicketIndexesAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "ticket-indexes", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Tickets_Status ON Tickets(Status)");
            await d.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Tickets_ParentId ON Tickets(ParentId)");
            await d.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Comments_TicketId ON Comments(TicketId)");
        });

    public async Task<List<TicketSummary>> ListTicketsAsync(string projectSlug, string? statusFilter = null, TicketPriority? priorityFilter = null, string? assignedTo = null, string? createdBy = null, string? search = null, int? parentId = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureSortOrderColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureParentIdColumnAsync(db);
        await EnsureScheduleColumnsAsync(db);
        await EnsureAgentUsageColumnsAsync(db);
        await EnsureTicketIndexesAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var query = db.Tickets.Include(t => t.Labels).AsQueryable();
        if (statusFilter is not null)
            query = query.Where(t => t.Status == statusFilter);
        if (priorityFilter.HasValue)
            query = query.Where(t => t.Priority == priorityFilter.Value);
        if (assignedTo is not null)
            query = query.Where(t => t.AssignedTo == assignedTo);
        if (createdBy is not null)
            query = query.Where(t => t.CreatedBy == createdBy);
        if (parentId is not null)
            query = query.Where(t => t.ParentId == parentId.Value);
        if (search is not null)
            query = query.Where(t => t.Title.Contains(search) || t.Description.Contains(search) || t.Comments.Any(c => c.Content.Contains(search)));

        var allTickets = await query
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .Select(t => new TicketSummary(
                t.Id, t.Title, t.Description, t.Status, t.Priority, t.SortOrder,
                t.AssignedTo, t.CreatedBy, t.CreatedAt, t.UpdatedAt,
                t.Labels,
                t.Comments.Count,
                t.Activities.Max(a => (DateTime?)a.CreatedAt),
                t.ParentId,
                new List<SubTicketInfo>())
                {
                    FireAt = t.FireAt,
                    ScheduleTarget = t.ScheduleTarget,
                    AgentTokens = t.AgentTokens,
                    AgentCostUsd = t.AgentCostUsd,
                    AgentCostEstimated = t.AgentCostEstimated,
                    PipelineId = t.PipelineId,
                    ColumnId = t.ColumnId,
                    BlocksParent = t.BlocksParent,
                })
            .ToListAsync();

        // Load children for ALL returned parents, ignoring the status filter so that
        // parents filtered by their own status still see children in other statuses.
        var parentIds = allTickets.Select(t => t.Id).ToHashSet();
        var childRows = parentIds.Count > 0
            ? await db.Tickets
                .Where(t => t.ParentId != null && parentIds.Contains(t.ParentId!.Value))
                .Select(t => new { t.ParentId, t.PipelineId, t.ColumnId, t.BlocksParent, t.Status, t.Id, t.Title, t.AssignedTo })
                .ToListAsync()
            : [];
        var childColumnIds = childRows.Where(x => x.ColumnId is not null).Select(x => x.ColumnId!.Value).Distinct().ToList();
        var childRoles = await db.BoardColumns.Where(c => childColumnIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Role);
        var subsByParent = childRows
            .GroupBy(x => x.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => new SubTicketInfo(x.Id, x.Title, x.Status, x.AssignedTo)
            {
                PipelineId = x.PipelineId,
                ColumnId = x.ColumnId,
                BlocksParent = x.BlocksParent,
                ColumnRole = x.ColumnId is int columnId && childRoles.TryGetValue(columnId, out var role) ? role : ColumnRole.Normal,
            }).ToList());

        return allTickets.Select(t => subsByParent.TryGetValue(t.Id, out var subs)
            ? t with { SubTickets = subs }
            : t).ToList();
    }

    public async Task<Ticket?> GetTicketAsync(string projectSlug, int ticketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureParentIdColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureScheduleColumnsAsync(db);
        await EnsureAgentUsageColumnsAsync(db);
        var ticket = await db.Tickets
            .Include(t => t.Comments.OrderBy(c => c.CreatedAt))
            .Include(t => t.Activities.OrderBy(a => a.CreatedAt))
            .Include(t => t.Labels)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return null;
        ticket.SubTickets = await db.Tickets
            .Where(t => t.ParentId == ticketId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .Select(t => new SubTicketInfo(t.Id, t.Title, t.Status, t.AssignedTo)
            {
                PipelineId = t.PipelineId,
                ColumnId = t.ColumnId,
                BlocksParent = t.BlocksParent,
            })
            .ToListAsync();
        var subColumnIds = ticket.SubTickets.Where(t => t.ColumnId is not null).Select(t => t.ColumnId!.Value).Distinct().ToList();
        var subRoles = await db.BoardColumns.Where(c => subColumnIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Role);
        ticket.SubTickets = ticket.SubTickets.Select(t => t with
        {
            ColumnRole = t.ColumnId is int columnId && subRoles.TryGetValue(columnId, out var role) ? role : ColumnRole.Normal,
        }).ToList();
        return ticket;
    }

    /// <summary>Loads only the fields needed by comment automation polling in one query.</summary>
    public async Task<List<(int TicketId, int CommentId, string Author)>> ListCommentCursorsAsync(
        string projectSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureTicketIndexesAsync(db);
        var rows = await db.Comments
            .AsNoTracking()
            .Select(c => new { c.TicketId, CommentId = c.Id, c.Author })
            .ToListAsync();
        return rows.Select(c => (c.TicketId, c.CommentId, c.Author)).ToList();
    }

    /// <summary>
    /// Accumulates a completed agent run's token usage onto the ticket. Durable counterpart of
    /// the in-memory run registry (whose runs are purged after 24h) — called by RunCostRecorder.
    /// </summary>
    public async Task AddAgentUsageAsync(string projectSlug, int ticketId, long tokens, double costUsd,
        bool costEstimated = false)
    {
        if (tokens <= 0 && costUsd <= 0) return;
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureSortOrderColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureParentIdColumnAsync(db);
        await EnsureScheduleColumnsAsync(db);
        await EnsureAgentUsageColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return;
        ticket.AgentTokens += tokens;
        ticket.AgentCostUsd += costUsd;
        ticket.AgentCostEstimated |= costEstimated;
        await db.SaveChangesAsync();
    }

    public async Task<Ticket> CreateTicketAsync(
        string projectSlug, string title, string description = "", string createdBy = "owner",
        string status = "Backlog", List<int>? labelIds = null,
        TicketPriority priority = TicketPriority.NiceToHave, string? assignedTo = null,
        int? parentId = null, int? pipelineId = null, int? columnId = null,
        bool blocksParent = true, bool recoveryOverride = false, string? overrideReason = null)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new InvalidOperationException("Le champ 'createdBy' est requis.");
        if (recoveryOverride && (!string.Equals(createdBy, "owner", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(overrideReason)))
            throw new InvalidOperationException("A recovery override requires createdBy 'owner' and a non-empty reason.");

        var creationGate = _creationGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await creationGate.WaitAsync();
        try
        {
        if (!string.IsNullOrEmpty(assignedTo) && !await _memberService.MemberExistsAsync(projectSlug, assignedTo))
            throw new InvalidOperationException($"Le membre '{assignedTo}' n'existe pas.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureParentIdColumnAsync(db);
        // Reject unknown statuses so the ticket is never created into a column the board
        // cannot render (invisible ticket). Canonical name matches BoardColumns.Name.
        var column = await RequireColumnAsync(db, status, pipelineId, columnId);
        status = column.Name;
        pipelineId = column.PipelineId;
        var blockedLimit = _automationStore is null
            ? 7
            : (await _automationStore.LoadAsync(projectSlug)).Config.BlockedTicketLimit ?? 7;
        var blockedColumnIds = await db.BoardColumns
            .Where(c => c.Role == ColumnRole.Blocked || c.Name == "Blocked")
            .Select(c => c.Id)
            .ToListAsync();
        var blockedCount = blockedLimit > 0 && blockedColumnIds.Count > 0
            ? await db.Tickets.CountAsync(t => t.ColumnId != null && blockedColumnIds.Contains(t.ColumnId.Value))
            : 0;
        if (blockedLimit > 0 && blockedCount >= blockedLimit && !recoveryOverride)
        {
            _logger?.LogWarning(
                "Ticket creation refused for project {ProjectSlug}: {BlockedCount} blocked tickets reached limit {BlockedLimit}",
                projectSlug, blockedCount, blockedLimit);
            throw new TicketCreationSaturationException(projectSlug, blockedCount, blockedLimit, blockedColumnIds);
        }
        if (parentId is not null)
        {
            var parentExists = await db.Tickets.AnyAsync(t => t.Id == parentId.Value);
            if (!parentExists)
                throw new InvalidOperationException($"Le ticket parent #{parentId} n'existe pas.");
        }
        var maxSort = await db.Tickets.Where(t => t.ColumnId == column.Id)
            .Select(t => (int?)t.SortOrder).MaxAsync() ?? -1;
        var ticket = new Ticket
        {
            Title = title,
            PipelineId = pipelineId.Value,
            ColumnId = column.Id,
            Description = description,
            CreatedBy = createdBy,
            Status = status,
            Priority = priority,
            SortOrder = maxSort + 1,
            AssignedTo = assignedTo,
            ParentId = parentId,
            BlocksParent = blocksParent,
        };
        ColumnAssignmentPolicy.Apply(ticket, null, column);
        if (labelIds is { Count: > 0 })
        {
            var labels = await db.Labels.Where(l => labelIds.Contains(l.Id)).ToListAsync();
            ticket.Labels = labels;
        }
        // Two SaveChanges (the entry needs the generated ticket id) — keep them atomic so a
        // crash can't produce a ticket without its creation activity.
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticket.Id,
            Author = createdBy,
            Text = "a créé le ticket"
        });
        if (recoveryOverride)
        {
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticket.Id,
                Author = createdBy,
                Text = $"used recovery saturation override: {overrideReason!.Trim()}"
            });
            _logger?.LogWarning(
                "Recovery saturation override used for project {ProjectSlug} by {Author}: {Reason}",
                projectSlug, createdBy, overrideReason.Trim());
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        TicketCreated?.Invoke(projectSlug, ticket.Id);
        return ticket;
        }
        finally
        {
            creationGate.Release();
        }
    }

    // Delegates to UpdateTicketAsync so there is exactly ONE write path for status
    // changes (column validation, Scheduled cleanup, activity, engine signal).
    public Task<Ticket?> MoveTicketAsync(string projectSlug, int ticketId, string newStatus, string author = "owner")
        => UpdateTicketAsync(projectSlug, ticketId, author: author, status: newStatus);

    /// <summary>
    /// Moves a ticket into the "Scheduled" column with a future <paramref name="fireAt"/> instant.
    /// The <see cref="ScheduledPromotionService"/> promotes it to <paramref name="targetStatus"/> once
    /// <paramref name="fireAt"/> is reached. This keeps calendar-dated work out of "Blocked".
    /// </summary>
    public async Task<Ticket?> ScheduleTicketAsync(string projectSlug, int ticketId, DateTime fireAt, string targetStatus = "Todo", string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        if (string.IsNullOrWhiteSpace(targetStatus))
            targetStatus = "Todo";
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureScheduleColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var scheduledColumn = await RequireScheduledColumnAsync(db, ticket.PipelineId);
        var targetColumn = await RequireColumnAsync(db, targetStatus, ticket.PipelineId);
        targetStatus = targetColumn.Name;
        var oldStatus = ticket.Status;
        var sourceColumn = ticket.ColumnId is int sourceColumnId
            ? await db.BoardColumns.FindAsync(sourceColumnId)
            : null;
        ColumnAssignmentPolicy.Apply(ticket, sourceColumn, scheduledColumn);
        ticket.Status = scheduledColumn.Name;
        ticket.ColumnId = scheduledColumn.Id;
        ticket.FireAt = fireAt;
        ticket.ScheduleTarget = targetStatus;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"a planifié le ticket pour {fireAt:yyyy-MM-dd HH:mm} UTC → {targetStatus}"
        });
        await db.SaveChangesAsync();
        if (!string.Equals(oldStatus, scheduledColumn.Name, StringComparison.OrdinalIgnoreCase))
            TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, scheduledColumn.Name);
        return ticket;
    }

    /// <summary>
    /// Returns the ids of waiting tickets whose <c>FireAt</c> is due (&lt;= <paramref name="now"/>).
    /// Processor-driven schedules may use any Waiting column name, so the role and persisted
    /// wake instant are authoritative rather than the legacy Scheduled/Planifié names.
    /// </summary>
    public async Task<List<int>> ListDueScheduledTicketIdsAsync(string projectSlug, DateTime now)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureScheduleColumnsAsync(db);
        var columns = await db.BoardColumns
            .Select(c => new { c.Id, c.Name, c.Role })
            .ToListAsync();
        var scheduledColumnIds = columns
            .Where(c => c.Role == ColumnRole.Waiting)
            .Select(c => c.Id)
            .ToArray();
        return await db.Tickets
            .Where(t => t.FireAt != null && t.FireAt <= now
                && t.ColumnId != null && scheduledColumnIds.Contains(t.ColumnId.Value))
            .OrderBy(t => t.FireAt)
            .Select(t => t.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Promotes a Scheduled ticket to its <c>ScheduleTarget</c> (default "Todo"), clears
    /// <c>FireAt</c>, and fires <see cref="TicketStatusChanged"/> so automations run. No-op if the
    /// ticket is no longer in a Waiting column, which prevents a consumed wake from resurrecting
    /// completed work.
    /// </summary>
    public async Task<Ticket?> PromoteScheduledAsync(string projectSlug, int ticketId, string author = "automation")
    {
        await _scheduledPromotionLock.WaitAsync();
        try
        {
            await using var db = _projectService.GetProjectDb(projectSlug);
            await EnsureActivityTableAsync(db);
            await EnsureScheduleColumnsAsync(db);
            await ColumnService.EnsureBoardColumnsTableAsync(db);
            var ticket = await db.Tickets.FindAsync(ticketId);
            if (ticket is null || ticket.FireAt is null || ticket.ColumnId is null)
                return null;
            var currentColumn = await db.BoardColumns.FindAsync(ticket.ColumnId.Value);
            if (currentColumn is null || currentColumn.Role != ColumnRole.Waiting)
                return null;
            var scheduledStatus = ticket.Status;
            var target = string.IsNullOrWhiteSpace(ticket.ScheduleTarget) ? "Todo" : ticket.ScheduleTarget!;
            BoardColumn targetColumn;
            try
            {
                targetColumn = await RequireColumnAsync(db, target, ticket.PipelineId);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Impossible de réveiller le ticket #{ticketId} du projet '{projectSlug}' : " +
                    $"la colonne cible planifiée '{target}' est absente du pipeline #{ticket.PipelineId}.", ex);
            }
            target = targetColumn.Name;
            ColumnAssignmentPolicy.Apply(ticket, currentColumn, targetColumn);
            ticket.Status = target;
            ticket.ColumnId = targetColumn.Id;
            ticket.FireAt = null;
            ticket.ScheduleTarget = null;
            ticket.UpdatedAt = DateTime.UtcNow;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"planification déclenchée : {scheduledStatus} → {target}"
            });
            await db.SaveChangesAsync();
            TicketStatusChanged?.Invoke(projectSlug, ticketId, scheduledStatus, target);
            return ticket;
        }
        finally
        {
            _scheduledPromotionLock.Release();
        }
    }

    /// <summary>
    /// Updates any combination of ticket fields — status included — in ONE SQLite write,
    /// so a hand-off like {status, assignedTo} can never be observed half-applied by the
    /// automation engine (backport analysis §2.2). Status changes go through the same
    /// semantics as the dedicated move path: target column validated, Scheduled cleanup,
    /// activity entry, and the TicketStatusChanged signal raised only after the full
    /// state is committed. <paramref name="expectedStatus"/> is optimistic concurrency:
    /// when set, the update only applies if the ticket is still in that status —
    /// otherwise <see cref="TicketTransitionConflictException"/> (mapped to HTTP 409).
    /// </summary>
    public async Task<Ticket?> UpdateTicketAsync(string projectSlug, int ticketId, string? title = null, string? description = null, string author = "owner", TicketPriority? priority = null, string? assignedTo = null, string? status = null, string? expectedStatus = null, int? pipelineId = null, int? columnId = null, bool? blocksParent = null, bool enforceRouting = false)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        if (!string.IsNullOrEmpty(assignedTo) && !await _memberService.MemberExistsAsync(projectSlug, assignedTo))
            throw new InvalidOperationException($"Le membre '{assignedTo}' n'existe pas.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureScheduleColumnsAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        BoardColumn? destination = null;
        if (columnId is not null)
        {
            destination = await db.BoardColumns.FindAsync(columnId.Value)
                ?? throw new InvalidOperationException($"La colonne #{columnId} n'existe pas.");
            if (pipelineId is not null && destination.PipelineId != pipelineId)
                throw new InvalidOperationException("La colonne cible n'appartient pas au pipeline indiqué.");
            if (status is not null && !string.Equals(status, destination.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le nom de colonne ne correspond pas à l'identifiant de colonne cible.");
            status = destination.Name;
        }
        else if (status is not null)
        {
            destination = await RequireColumnAsync(db, status, pipelineId ?? ticket.PipelineId);
            status = destination.Name;
        }

        if (expectedStatus is not null && !string.Equals(ticket.Status, expectedStatus, StringComparison.OrdinalIgnoreCase))
            throw new TicketTransitionConflictException(ticket.Status, expectedStatus);

        string? oldStatus = null;
        if (destination is not null && (ticket.ColumnId != destination.Id || ticket.PipelineId != destination.PipelineId
            || !string.Equals(ticket.Status, status, StringComparison.OrdinalIgnoreCase)))
        {
            oldStatus = ticket.Status;
            var source = ticket.ColumnId is int sourceColumnId
                ? await db.BoardColumns.FindAsync(sourceColumnId)
                : null;
            await EnsureRoutingAllowsManualMoveAsync(projectSlug, db, source, destination, enforceRouting);
            ticket.Status = status!;
            ticket.ColumnId = destination!.Id;
            ticket.PipelineId = destination.PipelineId;
            var assignmentChangedByRole = ColumnAssignmentPolicy.Apply(ticket, source, destination);
            if (ticket.FireAt is not null && !IsScheduledColumnName(destination.Name))
            {
                // Leaving Scheduled cancels the pending promotion — otherwise the stale
                // FireAt keeps showing a countdown badge and would fire instantly if re-scheduled.
                ticket.FireAt = null;
                ticket.ScheduleTarget = null;
            }
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"a déplacé le ticket : {oldStatus} → {status}"
            });
            if (assignmentChangedByRole)
            {
                db.ActivityEntries.Add(new ActivityEntry
                {
                    TicketId = ticketId,
                    Author = "KittyClaw",
                    Text = destination.Role == ColumnRole.OwnerAction
                        ? "requiert une action du Owner"
                        : "a retiré l’affectation Owner automatique"
                });
            }
        }

        if (title is not null && title != ticket.Title)
        {
            var old = ticket.Title;
            ticket.Title = title;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"a renommé le ticket : \"{old}\" → \"{title}\""
            });
        }
        if (description is not null && description != ticket.Description)
        {
            ticket.Description = description;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = "a modifié la description"
            });
        }
        if (priority is not null && priority != ticket.Priority)
        {
            var old = ticket.Priority;
            ticket.Priority = priority.Value;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"a changé la priorité : {PriorityLabel(old)} → {PriorityLabel(priority.Value)}"
            });
        }
        if (assignedTo is not null && assignedTo != ticket.AssignedTo)
        {
            var old = ticket.AssignedTo ?? "personne";
            ticket.AssignedTo = assignedTo.Length == 0 ? null : assignedTo;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"a assigné le ticket : {old} → {ticket.AssignedTo ?? "personne"}"
            });
        }
        if (blocksParent is not null && blocksParent != ticket.BlocksParent)
        {
            ticket.BlocksParent = blocksParent.Value;
            oldStatus ??= ticket.Status;
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = blocksParent.Value ? "a rendu ce sous-ticket bloquant" : "a rendu ce sous-ticket non bloquant"
            });
        }
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        // Signal AFTER commit: the engine can only ever observe the fully-written state.
        if (oldStatus is not null)
            TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, ticket.Status!);
        return ticket;
    }

    public async Task<bool> DeleteTicketAsync(string projectSlug, int ticketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureParentIdColumnAsync(db);
        var ticket = await db.Tickets
            .Include(t => t.Comments)
            .Include(t => t.Activities)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return false;
        // Unparent any children before deleting
        var children = await db.Tickets.Where(t => t.ParentId == ticketId).ToListAsync();
        foreach (var child in children)
            child.ParentId = null;
        db.Comments.RemoveRange(ticket.Comments);
        db.ActivityEntries.RemoveRange(ticket.Activities);
        db.Tickets.Remove(ticket);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetParentAsync(string projectSlug, int ticketId, int parentId, string author = "owner")
    {
        if (ticketId == parentId) return false;
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureParentIdColumnAsync(db);
        await EnsureActivityTableAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        var parent = await db.Tickets.FindAsync(parentId);
        if (ticket is null || parent is null) return false;
        // Prevent circular: parent must not itself be a child of ticketId
        if (parent.ParentId == ticketId) return false;
        ticket.ParentId = parentId;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"est devenu sous-ticket de #{parentId}"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UnparentAsync(string projectSlug, int ticketId, string author = "owner")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureParentIdColumnAsync(db);
        await EnsureActivityTableAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null || ticket.ParentId is null) return false;
        var oldParentId = ticket.ParentId.Value;
        ticket.ParentId = null;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"a été dissocié du ticket parent #{oldParentId}"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<Comment?> AddCommentAsync(string projectSlug, int ticketId, string? content, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Le champ 'content' est requis.");
        content = content.Trim();
        await using var db = _projectService.GetProjectDb(projectSlug);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var comment = new Comment
        {
            TicketId = ticketId,
            Content = content,
            Author = author
        };
        db.Comments.Add(comment);
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        TicketCommentAdded?.Invoke(projectSlug, ticketId, comment.Id, author, content);
        return comment;
    }

    public async Task<bool> SetTicketLabelsAsync(string projectSlug, int ticketId, List<int> labelIds)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        var ticket = await db.Tickets.Include(t => t.Labels).FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return false;
        var labels = await db.Labels.Where(l => labelIds.Contains(l.Id)).ToListAsync();
        ticket.Labels = labels;
        await db.SaveChangesAsync();
        return true;
    }

    // Serializes incremental label patches in-process: every API call goes through this
    // singleton, so the lock is enough to make concurrent read-merge-write cycles safe.
    private readonly SemaphoreSlim _labelPatchLock = new(1, 1);

    /// <summary>
    /// Incremental label patch (backport analysis §2.3): merges <paramref name="add"/> and
    /// <paramref name="remove"/> (by name, case-insensitive) into the ticket's CURRENT
    /// labels server-side, in one write. Unlike the replace-all SetTicketLabelsAsync, two
    /// agents adding different labels concurrently both keep theirs — no lost update, and
    /// no client is ever forced to send back "the whole list" it may hold stale. Unknown
    /// names in add are created on the fly (UI default color); unknown names in remove are
    /// ignored.
    /// </summary>
    public async Task<Ticket?> PatchTicketLabelsAsync(string projectSlug, int ticketId, IReadOnlyList<string> add, IReadOnlyList<string> remove, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        var addNames = add.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var removeNames = remove.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addNames.Count == 0 && removeNames.Count == 0)
            throw new InvalidOperationException("Au moins un label dans 'add' ou 'remove' est requis.");

        await _labelPatchLock.WaitAsync();
        try
        {
            await using var db = _projectService.GetProjectDb(projectSlug);
            await EnsureLabelTablesAsync(db);
            await EnsureActivityTableAsync(db);
            var ticket = await db.Tickets.Include(t => t.Labels).FirstOrDefaultAsync(t => t.Id == ticketId);
            if (ticket is null) return null;

            var allLabels = await db.Labels.ToListAsync();
            var current = ticket.Labels.ToList();

            foreach (var name in addNames)
            {
                if (current.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var label = allLabels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                if (label is null)
                {
                    label = new Label { Name = name, Color = "#6366f1" };
                    db.Labels.Add(label);
                    allLabels.Add(label);
                }
                current.Add(label);
            }
            foreach (var name in removeNames)
                current.RemoveAll(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

            ticket.Labels = current;
            ticket.UpdatedAt = DateTime.UtcNow;
            var parts = new List<string>();
            if (addNames.Count > 0) parts.Add(string.Join(", ", addNames.Select(n => $"+{n}")));
            if (removeNames.Count > 0) parts.Add(string.Join(", ", removeNames.Select(n => $"-{n}")));
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = author,
                Text = $"a modifié les labels : {string.Join(" ", parts)}"
            });
            await db.SaveChangesAsync();
            return ticket;
        }
        finally
        {
            _labelPatchLock.Release();
        }
    }

    public async Task<bool> UpdateCommentAsync(string projectSlug, int ticketId, int commentId, string? content, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Le champ 'content' est requis.");
        content = content.Trim();
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        var comment = await db.Comments.FindAsync(commentId);
        if (comment is null || comment.TicketId != ticketId) return false;
        comment.Content = content;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = "a modifié un commentaire"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteCommentAsync(string projectSlug, int ticketId, int commentId, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        var comment = await db.Comments.FindAsync(commentId);
        if (comment is null || comment.TicketId != ticketId) return false;
        db.Comments.Remove(comment);
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = "a supprimé un commentaire"
        });
        await db.SaveChangesAsync();
        return true;
    }

    public async Task ReorderTicketAsync(string projectSlug, int ticketId, string newStatus, int targetIndex, bool enforceRouting = false)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureSortOrderColumnAsync(db);
        await EnsureActivityTableAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return;
        // Same column gate as MoveTicketAsync — drag-and-drop must not park tickets in a
        // phantom status the board does not render. Resolve inside the ticket's pipeline.
        var destination = await RequireColumnAsync(db, newStatus, ticket.PipelineId);
        newStatus = destination.Name;

        var oldStatus = ticket.Status;
        var statusChanged = !string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase);
        var source = ticket.ColumnId is int sourceColumnId
            ? await db.BoardColumns.FindAsync(sourceColumnId)
            : null;
        await EnsureRoutingAllowsManualMoveAsync(projectSlug, db, source, destination, enforceRouting);
        ticket.Status = newStatus;
        ticket.ColumnId = destination.Id;
        ColumnAssignmentPolicy.Apply(ticket, source, destination);
        ticket.UpdatedAt = DateTime.UtcNow;
        if (statusChanged && ticket.FireAt is not null && !IsScheduledColumnName(destination.Name))
        {
            ticket.FireAt = null;
            ticket.ScheduleTarget = null;
        }

        // Get all tickets in the target column (excluding the moved ticket)
        var columnTickets = await db.Tickets
            .Where(t => t.ColumnId == destination.Id && t.Id != ticketId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .ToListAsync();

        // Clamp target index
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex > columnTickets.Count) targetIndex = columnTickets.Count;

        // Insert ticket at target position and reassign sort orders
        columnTickets.Insert(targetIndex, ticket);
        for (int i = 0; i < columnTickets.Count; i++)
            columnTickets[i].SortOrder = i;

        if (statusChanged)
        {
            db.ActivityEntries.Add(new ActivityEntry
            {
                TicketId = ticketId,
                Author = "owner",
                Text = $"a déplacé le ticket : {oldStatus} → {newStatus}"
            });
        }

        await db.SaveChangesAsync();
        if (statusChanged)
            TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, newStatus);
    }

    private async Task EnsureRoutingAllowsManualMoveAsync(
        string projectSlug,
        TodoDbContext db,
        BoardColumn? source,
        BoardColumn destination,
        bool enforceRouting)
    {
        if (!enforceRouting || source is null || source.Id == destination.Id || _columnProcessors is null) return;
        var policy = ColumnRoutingPolicy.From(await _columnProcessors.GetAsync(projectSlug, source.Id));
        if (policy.Allows(source.Id, destination.Id)) return;

        var allowedNames = await db.BoardColumns
            .Where(column => policy.AllowedTargetColumnIds.Contains(column.Id))
            .OrderBy(column => column.PipelineId).ThenBy(column => column.SortOrder)
            .Select(column => column.Name)
            .ToListAsync();
        throw new InvalidOperationException(
            $"Le routage de la colonne '{source.Name}' n'autorise pas un déplacement manuel vers '{destination.Name}'. " +
            $"Destinations prévues : {string.Join(", ", allowedNames)}.");
    }

    private static string PriorityLabel(TicketPriority p) => p switch
    {
        TicketPriority.Idea => "Idea",
        TicketPriority.NiceToHave => "Nice to have",
        TicketPriority.Required => "Required",
        TicketPriority.Critical => "Critical",
        _ => p.ToString()
    };

    public async Task AddActivityAsync(string projectSlug, int ticketId, string text, string author = "automation")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return;
        db.ActivityEntries.Add(new ActivityEntry { TicketId = ticketId, Author = author, Text = text });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns tickets where @handle appears in description or comments,
    /// optionally filtered by date range.
    /// </summary>
    public async Task<List<Ticket>> ListMentionedTicketsAsync(string projectSlug, string handle, DateTime? since = null, DateTime? until = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        await EnsureSortOrderColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureActivityTableAsync(db);

        var mentionPattern = $"@{handle}";

        var tickets = await db.Tickets
            .Include(t => t.Labels)
            .Include(t => t.Comments)
            .Where(t => t.Description.Contains(mentionPattern)
                || t.Comments.Any(c => c.Content.Contains(mentionPattern)))
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();

        if (since.HasValue)
            tickets = tickets.Where(t => t.UpdatedAt >= since.Value).ToList();
        if (until.HasValue)
            tickets = tickets.Where(t => t.UpdatedAt <= until.Value).ToList();

        return tickets;
    }
}
