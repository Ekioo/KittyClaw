using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public class TicketService
{
    private readonly ProjectService _projectService;
    private readonly MemberService _memberService;

    /// <summary>
    /// Raised after a ticket's status has been persisted.
    /// Parameters: (projectSlug, ticketId, fromStatus, toStatus)
    /// </summary>
    public event Action<string, int, string, string>? TicketStatusChanged;

    /// <summary>
    /// Raised immediately after a comment is persisted.
    /// Parameters: (projectSlug, ticketId, author, content)
    /// </summary>
    public event Action<string, int, string, string>? TicketCommentAdded;

    public TicketService(ProjectService projectService, MemberService memberService)
    {
        _projectService = projectService;
        _memberService = memberService;
    }

    // Ensures the ActivityEntries table exists (for databases created before this feature)
    private static Task EnsureActivityTableAsync(TodoDbContext db) =>
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
    private static Task EnsureAgentUsageColumnsAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "tickets-agent-usage", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AgentTokens INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE Tickets ADD COLUMN AgentCostUsd REAL NOT NULL DEFAULT 0");
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
                    AgentCostUsd = t.AgentCostUsd
                })
            .ToListAsync();

        // Load children for ALL returned parents, ignoring the status filter so that
        // parents filtered by their own status still see children in other statuses.
        var parentIds = allTickets.Select(t => t.Id).ToHashSet();
        var childRows = parentIds.Count > 0
            ? await db.Tickets
                .Where(t => t.ParentId != null && parentIds.Contains(t.ParentId!.Value))
                .Select(t => new { t.ParentId, Info = new SubTicketInfo(t.Id, t.Title, t.Status, t.AssignedTo) })
                .ToListAsync()
            : [];
        var subsByParent = childRows
            .GroupBy(x => x.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Info).ToList());

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
            .Select(t => new SubTicketInfo(t.Id, t.Title, t.Status, t.AssignedTo))
            .ToListAsync();
        return ticket;
    }

    /// <summary>
    /// Accumulates a completed agent run's token usage onto the ticket. Durable counterpart of
    /// the in-memory run registry (whose runs are purged after 24h) — called by RunCostRecorder.
    /// </summary>
    public async Task AddAgentUsageAsync(string projectSlug, int ticketId, long tokens, double costUsd)
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
        await db.SaveChangesAsync();
    }

    public async Task<Ticket> CreateTicketAsync(string projectSlug, string title, string description = "", string createdBy = "owner", string status = "Backlog", List<int>? labelIds = null, TicketPriority priority = TicketPriority.NiceToHave, string? assignedTo = null, int? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new InvalidOperationException("Le champ 'createdBy' est requis.");
        if (!string.IsNullOrEmpty(assignedTo) && !await _memberService.MemberExistsAsync(projectSlug, assignedTo))
            throw new InvalidOperationException($"Le membre '{assignedTo}' n'existe pas.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureAssignedToColumnAsync(db);
        await EnsureParentIdColumnAsync(db);
        if (parentId is not null)
        {
            var parentExists = await db.Tickets.AnyAsync(t => t.Id == parentId.Value);
            if (!parentExists)
                throw new InvalidOperationException($"Le ticket parent #{parentId} n'existe pas.");
        }
        var maxSort = await db.Tickets.Where(t => t.Status == status).Select(t => (int?)t.SortOrder).MaxAsync() ?? -1;
        var ticket = new Ticket
        {
            Title = title,
            Description = description,
            CreatedBy = createdBy,
            Status = status,
            Priority = priority,
            SortOrder = maxSort + 1,
            AssignedTo = assignedTo,
            ParentId = parentId
        };
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
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ticket;
    }

    public async Task<Ticket?> MoveTicketAsync(string projectSlug, int ticketId, string newStatus, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var columnExists = await db.BoardColumns.AnyAsync(c => c.Name == newStatus);
        if (!columnExists)
            throw new InvalidOperationException($"La colonne '{newStatus}' n'existe pas.");
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var oldStatus = ticket.Status;
        if (string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase))
            return ticket; // already in target status — no-op
        ticket.Status = newStatus;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"a déplacé le ticket : {oldStatus} → {newStatus}"
        });
        await db.SaveChangesAsync();
        TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, newStatus);
        return ticket;
    }

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
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var scheduledExists = await db.BoardColumns.AnyAsync(c => c.Name == "Scheduled");
        if (!scheduledExists)
            throw new InvalidOperationException("La colonne 'Scheduled' n'existe pas.");
        var targetExists = await db.BoardColumns.AnyAsync(c => c.Name == targetStatus);
        if (!targetExists)
            throw new InvalidOperationException($"La colonne cible '{targetStatus}' n'existe pas.");
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var oldStatus = ticket.Status;
        ticket.Status = "Scheduled";
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
        if (!string.Equals(oldStatus, "Scheduled", StringComparison.OrdinalIgnoreCase))
            TicketStatusChanged?.Invoke(projectSlug, ticketId, oldStatus, "Scheduled");
        return ticket;
    }

    /// <summary>
    /// Returns the ids of Scheduled tickets whose <c>FireAt</c> is due (&lt;= <paramref name="now"/>).
    /// </summary>
    public async Task<List<int>> ListDueScheduledTicketIdsAsync(string projectSlug, DateTime now)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureScheduleColumnsAsync(db);
        return await db.Tickets
            .Where(t => t.Status == "Scheduled" && t.FireAt != null && t.FireAt <= now)
            .OrderBy(t => t.FireAt)
            .Select(t => t.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Promotes a Scheduled ticket to its <c>ScheduleTarget</c> (default "Todo"), clears
    /// <c>FireAt</c>, and fires <see cref="TicketStatusChanged"/> so automations (e.g. a
    /// <c>statusChange { from: "Scheduled" }</c> trigger) run. No-op if the ticket is not Scheduled.
    /// </summary>
    public async Task<Ticket?> PromoteScheduledAsync(string projectSlug, int ticketId, string author = "automation")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureScheduleColumnsAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null || !string.Equals(ticket.Status, "Scheduled", StringComparison.OrdinalIgnoreCase))
            return null;
        var target = string.IsNullOrWhiteSpace(ticket.ScheduleTarget) ? "Todo" : ticket.ScheduleTarget!;
        var targetExists = await db.BoardColumns.AnyAsync(c => c.Name == target);
        if (!targetExists) target = "Todo";
        ticket.Status = target;
        ticket.FireAt = null;
        ticket.ScheduleTarget = null;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"planification déclenchée : Scheduled → {target}"
        });
        await db.SaveChangesAsync();
        TicketStatusChanged?.Invoke(projectSlug, ticketId, "Scheduled", target);
        return ticket;
    }

    public async Task<Ticket?> UpdateTicketAsync(string projectSlug, int ticketId, string? title = null, string? description = null, string author = "owner", TicketPriority? priority = null, string? assignedTo = null)
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
        if (!string.IsNullOrEmpty(assignedTo) && !await _memberService.MemberExistsAsync(projectSlug, assignedTo))
            throw new InvalidOperationException($"Le membre '{assignedTo}' n'existe pas.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureAssignedToColumnAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;

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
        ticket.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
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

    public async Task<Comment?> AddCommentAsync(string projectSlug, int ticketId, string content, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
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
        TicketCommentAdded?.Invoke(projectSlug, ticketId, author, content);
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

    public async Task<bool> UpdateCommentAsync(string projectSlug, int ticketId, int commentId, string content, string author = "owner")
    {
        if (string.IsNullOrWhiteSpace(author))
            throw new InvalidOperationException("Le champ 'author' est requis.");
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

    public async Task ReorderTicketAsync(string projectSlug, int ticketId, string newStatus, int targetIndex)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureSortOrderColumnAsync(db);
        await EnsureActivityTableAsync(db);

        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return;

        var oldStatus = ticket.Status;
        var statusChanged = oldStatus != newStatus;
        ticket.Status = newStatus;
        ticket.UpdatedAt = DateTime.UtcNow;

        // Get all tickets in the target column (excluding the moved ticket)
        var columnTickets = await db.Tickets
            .Where(t => t.Status == newStatus && t.Id != ticketId)
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
