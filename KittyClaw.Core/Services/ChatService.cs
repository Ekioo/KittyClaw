using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed class ChatService
{
    private readonly ProjectService _projects;

    public ChatService(ProjectService projects)
    {
        _projects = projects;
    }

    private static async Task EnsureTableAsync(TodoDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ChatMessages (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                TargetSlug TEXT NOT NULL,
                Role TEXT NOT NULL,
                Text TEXT NOT NULL,
                ToolName TEXT NULL,
                Detail TEXT NULL,
                CreatedAt TEXT NOT NULL
            )
        """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ChatMessages_Target ON ChatMessages(TargetSlug, CreatedAt)");
        // Image paste support (#115): persist a JSON blob of data URLs so the drawer can
        // re-render thumbnails when the user reopens a past conversation.
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE ChatMessages ADD COLUMN imagesJson TEXT NULL"); }
        catch { /* already exists */ }
    }

    public async Task<List<ChatMessageRow>> ListAsync(string projectSlug, string targetSlug)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        return await db.ChatMessages
            .Where(m => m.TargetSlug == targetSlug)
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    public async Task AppendAsync(string projectSlug, string targetSlug, string role, string text,
                                   string? toolName = null, string? detail = null)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        db.ChatMessages.Add(new ChatMessageRow
        {
            TargetSlug = targetSlug,
            Role = role,
            Text = text,
            ToolName = toolName,
            Detail = detail,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        });
        await db.SaveChangesAsync();
    }

    public async Task ClearAsync(string projectSlug, string targetSlug)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var rows = await db.ChatMessages.Where(m => m.TargetSlug == targetSlug).ToListAsync();
        if (rows.Count == 0) return;
        db.ChatMessages.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    public async Task<string?> LastTargetAsync(string projectSlug)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        return await db.ChatMessages
            .OrderByDescending(m => m.Id)
            .Select(m => m.TargetSlug)
            .FirstOrDefaultAsync();
    }
}
