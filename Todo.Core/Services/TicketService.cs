using Microsoft.EntityFrameworkCore;
using Todo.Core.Data;
using Todo.Core.Models;

namespace Todo.Core.Services;

public class TicketService
{
    private readonly ProjectService _projectService;

    public TicketService(ProjectService projectService)
    {
        _projectService = projectService;
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

    public async Task<List<Ticket>> ListTicketsAsync(string projectSlug, TicketStatus? statusFilter = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureLabelTablesAsync(db);
        var query = db.Tickets.Include(t => t.Labels).AsQueryable();
        if (statusFilter.HasValue)
            query = query.Where(t => t.Status == statusFilter.Value);
        return await query.OrderBy(t => t.CreatedAt).ToListAsync();
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

    public async Task<Ticket> CreateTicketAsync(string projectSlug, string title, string description = "", string createdBy = "owner", TicketStatus status = TicketStatus.Backlog, List<int>? labelIds = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        await EnsureLabelTablesAsync(db);
        var ticket = new Ticket
        {
            Title = title,
            Description = description,
            CreatedBy = createdBy,
            Status = status
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

    public async Task<Ticket?> MoveTicketAsync(string projectSlug, int ticketId, TicketStatus newStatus, string author = "owner")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
        var ticket = await db.Tickets.FindAsync(ticketId);
        if (ticket is null) return null;
        var oldStatus = ticket.Status;
        ticket.Status = newStatus;
        ticket.UpdatedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticketId,
            Author = author,
            Text = $"a déplacé le ticket : {StatusLabel(oldStatus)} → {StatusLabel(newStatus)}"
        });
        await db.SaveChangesAsync();
        return ticket;
    }

    public async Task<Ticket?> UpdateTicketAsync(string projectSlug, int ticketId, string? title = null, string? description = null, string author = "owner")
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureActivityTableAsync(db);
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

    private static string StatusLabel(TicketStatus s) => s switch
    {
        TicketStatus.Backlog => "Backlog",
        TicketStatus.Todo => "Todo",
        TicketStatus.InProgress => "In Progress",
        TicketStatus.Blocked => "Blocked",
        TicketStatus.OwnerReview => "Owner Review",
        TicketStatus.Done => "Done",
        _ => s.ToString()
    };
}
