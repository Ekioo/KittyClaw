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

    private static Task EnsureTableAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "chat-messages", static async d =>
        {
            await d.Database.ExecuteSqlRawAsync("""
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
            await d.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_ChatMessages_Target ON ChatMessages(TargetSlug, CreatedAt)");
            // Image paste support (#115): persist a JSON blob of data URLs so the drawer can
            // re-render thumbnails when the user reopens a past conversation.
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ChatMessages ADD COLUMN imagesJson TEXT NULL");
            await d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ChatMemoryConsolidations (
                    TargetSlug TEXT NOT NULL PRIMARY KEY,
                    LastConsolidatedMessageId INTEGER NOT NULL DEFAULT 0,
                    Status TEXT NOT NULL DEFAULT 'Pending',
                    AttemptCount INTEGER NOT NULL DEFAULT 0,
                    NextAttemptAt TEXT NULL,
                    LastError TEXT NULL,
                    UpdatedAt TEXT NOT NULL
                )
            """);
        });

    public async Task<List<ChatMessageRow>> ListAsync(string projectSlug, string targetSlug)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        return await db.ChatMessages
            .Where(m => m.TargetSlug == targetSlug)
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    public async Task<bool> AnyAsync(string projectSlug, string targetSlug)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        return await db.ChatMessages.AnyAsync(m => m.TargetSlug == targetSlug);
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
        if (rows.Count > 0) db.ChatMessages.RemoveRange(rows);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ChatMemoryConsolidations WHERE TargetSlug = {targetSlug}");
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

    public async Task<List<ChatMemoryCandidate>> ListMemoryCandidatesAsync(
        string projectSlug, DateTime eligibleBefore, DateTime now)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.TargetSlug, MAX(m.Id), MAX(m.CreatedAt),
                   COALESCE(s.LastConsolidatedMessageId, 0), COALESCE(s.AttemptCount, 0)
            FROM ChatMessages m
            LEFT JOIN ChatMemoryConsolidations s ON s.TargetSlug = m.TargetSlug
            WHERE (s.NextAttemptAt IS NULL OR s.NextAttemptAt <= $now)
            GROUP BY m.TargetSlug
            HAVING MAX(m.Id) > COALESCE(s.LastConsolidatedMessageId, 0)
               AND MAX(m.CreatedAt) <= $eligibleBefore
            """;
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$eligibleBefore", eligibleBefore.ToString("o")));
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$now", now.ToString("o")));
        var result = new List<ChatMemoryCandidate>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new(reader.GetString(0), reader.GetInt32(1), DateTime.Parse(reader.GetString(2)),
                reader.GetInt32(3), reader.GetInt32(4)));
        return result;
    }

    public async Task<List<ChatMessageRow>> ListSegmentAsync(
        string projectSlug, string targetSlug, int afterId, int throughId)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        return await db.ChatMessages.AsNoTracking()
            .Where(m => m.TargetSlug == targetSlug && m.Id > afterId && m.Id <= throughId)
            .OrderBy(m => m.Id).ToListAsync();
    }

    public async Task RecordMemoryResultAsync(string projectSlug, string targetSlug, int throughId,
        string status, int attemptCount, string? error, DateTime? nextAttemptAt)
    {
        await using var db = _projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var nextAttemptText = nextAttemptAt?.ToString("o");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ChatMemoryConsolidations
                (TargetSlug, LastConsolidatedMessageId, Status, AttemptCount, NextAttemptAt, LastError, UpdatedAt)
            VALUES ({targetSlug}, {throughId}, {status}, {attemptCount},
                    {nextAttemptText}, {error}, {DateTime.UtcNow.ToString("o")})
            ON CONFLICT(TargetSlug) DO UPDATE SET
                LastConsolidatedMessageId = excluded.LastConsolidatedMessageId,
                Status = excluded.Status, AttemptCount = excluded.AttemptCount,
                NextAttemptAt = excluded.NextAttemptAt, LastError = excluded.LastError,
                UpdatedAt = excluded.UpdatedAt
            """);
    }

    public async Task RecordMemoryFailureAsync(string projectSlug, string targetSlug, int checkpoint,
        int attemptCount, string error, DateTime nextAttemptAt) =>
        await RecordMemoryResultAsync(projectSlug, targetSlug, checkpoint, "Failed", attemptCount,
            error, nextAttemptAt);
}

public sealed record ChatMemoryCandidate(string TargetSlug, int LatestMessageId, DateTime LastMessageAt,
    int LastConsolidatedMessageId, int AttemptCount);
