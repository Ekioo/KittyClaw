using Microsoft.EntityFrameworkCore;
using Todo.Core.Data;
using Todo.Core.Models;

namespace Todo.Core.Services;

public class TicketService
{
    private readonly ProjectService _projectService;
    private readonly MemberService _memberService;

    public TicketService(ProjectService projectService, MemberService memberService)
    {
        _projectService = projectService;
        _memberService = memberService;
    }

    // Ensures the ActivityEntries table exists (for databases created before this feature)
    private static async Task EnsureActivityTableAsync(TodoDbContext db) =>
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ActivityEntries (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                TicketId INTEGER NOT NULL,
                Author TEXT NOT NULL,
                Text TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            )
        """);

    private static async Task EnsureLabelTablesAsync(TodoDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Labels (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Color TEXT NOT NULL DEFAULT '#6366f1'
            )
        """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS TicketLabels (
                TicketsId INTEGER NOT NULL,
                LabelsId INTEGER NOT NULL,
                PRIMARY KEY (TicketsId, LabelsId)
            )
        """);
    }

    private static async Task EnsureSortOrderColumnAsync(TodoDbContext db)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tickets ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0");
        }
        catch { /* column already exists */ }
    }

    private static async Task EnsureAssignedToColumnAsync(TodoDbContext db)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tickets ADD COLUMN AssignedTo TEXT NULL");
        }
        catch { /* column already exists */ }
    }

    public async Task<List<TicketSummary>> ListTicketsAsync(string projectSlug, string? statusFilter = null, TicketPriority? priorityFilter = null, string? assignedTo = null, string? createdBy = null, string? search = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureSortOrderColumnAsync(db);
        await EnsureAssignedToColumnAsync(db);
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
        if (search is not null)
            query = query.Where(t => t.Title.Contains(search) || t.Description.Contains(search) || t.Comments.Any(c => c.Content.Contains(search)));
        return await query
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .Select(t => new TicketSummary(
                t.Id, t.Title, t.Description, t.Status, t.Priority, t.SortOrder,
                t.AssignedTo, t.CreatedBy, t.CreatedAt, t.UpdatedAt,
                t.Labels,
                t.Comments.Count,
                t.Activities.Max(a => (DateTime?)a.CreatedAt)))
            .ToListAsync();
    }

    public async Task<Ticket?> GetTicketAsync(string projectSlug, int ticketId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        return await db.Tickets
            .Include(t => t.Comments.OrderBy(c => c.CreatedAt))
            .Include(t => t.Activities.OrderBy(a => a.CreatedAt))
            .Include(t => t.Labels)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
    }

    public async Task<Ticket> CreateTicketAsync(string projectSlug, string title, string description = "", string createdBy = "owner", string status = "Backlog", List<int>? labelIds = null, TicketPriority priority = TicketPriority.NiceToHave, string? assignedTo = null)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new InvalidOperationException("Le champ 'createdBy' est requis.");
        if (!string.IsNullOrEmpty(assignedTo) && !await _memberService.MemberExistsAsync(projectSlug, assignedTo))
            throw new InvalidOperationException($"Le membre '{assignedTo}' n'existe pas.");
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        await EnsureAssignedToColumnAsync(db);
        var maxSort = await db.Tickets.Where(t => t.Status == status).Select(t => (int?)t.SortOrder).MaxAsync() ?? -1;
        var ticket = new Ticket
        {
            Title = title,
            Description = description,
            CreatedBy = createdBy,
            Status = status,
            Priority = priority,
            SortOrder = maxSort + 1,
            AssignedTo = assignedTo
        };
        if (labelIds is { Count: > 0 })
        {
            var labels = await db.Labels.Where(l => labelIds.Contains(l.Id)).ToListAsync();
            ticket.Labels = labels;
        }
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticket.Id,
            Author = createdBy,
            Text = "a créé le ticket"
        });
        await db.SaveChangesAsync();
        return ticket;
    }

    public async Task<Ticket?> MoveTicketAsync(string projectSlug, int ticketId, string newStatus, string author = "owner")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var columnExists = await db.BoardColumns.AnyAsync(c => c.Name == newStatus);
        if (!columnExists)
            throw new InvalidOperationException($"La colonne '{newStatus}' n'existe pas.");
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var oldStatus = ticket.Status;
        ticket.Status = newStatus;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"a déplacé le ticket : {oldStatus} → {newStatus}"
        });
        await db.SaveChangesAsync();
        return ticket;
    }

    public async Task<Ticket?> UpdateTicketAsync(string projectSlug, int ticketId, string? title = null, string? description = null, string author = "owner", TicketPriority? priority = null, string? assignedTo = null)
    {
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
        var ticket = await db.Tickets
            .Include(t => t.Comments)
            .Include(t => t.Activities)
            .FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return false;
        db.Comments.RemoveRange(ticket.Comments);
        db.ActivityEntries.RemoveRange(ticket.Activities);
        db.Tickets.Remove(ticket);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<Comment?> AddCommentAsync(string projectSlug, int ticketId, string content, string author = "owner")
    {
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
    }

    private static string PriorityLabel(TicketPriority p) => p switch
    {
        TicketPriority.Idea => "Idea",
        TicketPriority.NiceToHave => "Nice to have",
        TicketPriority.Required => "Required",
        TicketPriority.Critical => "Critical",
        _ => p.ToString()
    };

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
