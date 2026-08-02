using System.Text.Json;
using Microsoft.Data.Sqlite;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public enum AutomationQueueStatus
{
    Pending,
    Running,
    Completed,
    Skipped,
    Failed,
    Cancelled,
}

public sealed record AutomationQueueEntry(
    long Id,
    int TicketId,
    string AutomationId,
    string AutomationName,
    string OccurrenceId,
    long QueueOrder,
    AutomationQueueStatus Status,
    int Attempts,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    DateTime? LeaseUntil,
    DateTime? AvailableAt,
    string? Reason,
    int ExecutableAhead,
    string AutomationSnapshot);

/// <summary>Durable, per-project FIFO for complete ticketInColumn automation chains.</summary>
public sealed class AutomationQueueStore
{
    internal static readonly TimeSpan OccurrenceWindow = TimeSpan.FromMinutes(10);
    internal const string LoopProtectionReason =
        "Cancelled by loop protection: a column transition repeated for this ticket within 10 minutes.";

    private readonly ProjectService _projects;

    public AutomationQueueStore(ProjectService projects) => _projects = projects;

    /// <summary>
    /// Records the ticket's current column even when no automation watches it. This keeps
    /// logical column occurrences correct when a ticket leaves a watched column and later
    /// re-enters it through an unwatched one.
    /// </summary>
    public async Task ObserveColumnAsync(
        string slug, int ticketId, string columnName, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        EnsureSchema(conn);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var occurrenceId = await ResolveOccurrenceAsync(conn, tx, ticketId, columnName, ct);
        await UpsertPresenceAsync(conn, tx, ticketId, columnName, occurrenceId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AutomationQueueEntry>> EnqueueAsync(
        string slug, Ticket ticket, IEnumerable<Automation> automations, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        EnsureSchema(conn);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var occurrenceId = await ResolveOccurrenceAsync(conn, tx, ticket.Id, ticket.Status, ct);
        await UpsertPresenceAsync(conn, tx, ticket.Id, ticket.Status, occurrenceId, ct);
        var loopProtected = await IsRepeatedTransitionAsync(
            conn, tx, ticket.Id, occurrenceId, DateTime.UtcNow.Subtract(OccurrenceWindow), ct);

        foreach (var automation in automations)
        {
            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR IGNORE INTO automation_queue
                    (TicketId, AutomationId, AutomationName, OccurrenceId, Status, Attempts, CreatedAt,
                     FinishedAt, Reason, AutomationSnapshot)
                VALUES(@ticket,@automation,@name,@occurrence,@status,0,@now,@finished,@reason,@snapshot)
                """;
            insert.Parameters.AddWithValue("@ticket", ticket.Id);
            insert.Parameters.AddWithValue("@automation", automation.Id);
            insert.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(automation.Name) ? automation.Id : automation.Name);
            insert.Parameters.AddWithValue("@occurrence", occurrenceId);
            insert.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("@status", loopProtected ? "Cancelled" : "Pending");
            insert.Parameters.AddWithValue("@finished", loopProtected ? DateTime.UtcNow.ToString("O") : DBNull.Value);
            insert.Parameters.AddWithValue("@reason", loopProtected ? LoopProtectionReason : DBNull.Value);
            insert.Parameters.AddWithValue("@snapshot", JsonSerializer.Serialize(automation, AutomationStore.JsonOptions));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await ListForTicketAsync(slug, ticket.Id, ct);
    }

    private static async Task<bool> IsRepeatedTransitionAsync(
        SqliteConnection conn, SqliteTransaction tx, int ticketId, string occurrenceId,
        DateTime windowStart, CancellationToken ct)
    {
        await using var count = conn.CreateCommand();
        count.Transaction = tx;
        count.CommandText = """
            SELECT COUNT(*)
            FROM automation_column_occurrences current
            JOIN automation_column_occurrences prior
              ON prior.TicketId=current.TicketId
             AND prior.ColumnName=current.ColumnName
             AND prior.PreviousColumnName=current.PreviousColumnName
             AND prior.OccurrenceId<>current.OccurrenceId
             AND prior.ObservedAt>=@windowStart
            WHERE current.TicketId=@ticket AND current.OccurrenceId=@occurrence
              AND current.PreviousColumnName IS NOT NULL
            """;
        count.Parameters.AddWithValue("@ticket", ticketId);
        count.Parameters.AddWithValue("@occurrence", occurrenceId);
        count.Parameters.AddWithValue("@windowStart", windowStart.ToString("O"));
        return Convert.ToInt32(await count.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<string> ResolveOccurrenceAsync(
        SqliteConnection conn, SqliteTransaction tx, int ticketId, string columnName, CancellationToken ct)
    {
        await using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT ColumnName, OccurrenceId FROM automation_column_presence WHERE TicketId=@ticket";
        read.Parameters.AddWithValue("@ticket", ticketId);
        string? previousColumn = null;
        string? previousOccurrence = null;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                previousColumn = reader.GetString(0);
                previousOccurrence = reader.GetString(1);
            }
        }

        if (string.Equals(previousColumn, columnName, StringComparison.OrdinalIgnoreCase))
            return previousOccurrence!;

        var occurrenceId = Guid.NewGuid().ToString("N");
        await using var record = conn.CreateCommand();
        record.Transaction = tx;
        record.CommandText = """
            INSERT OR IGNORE INTO automation_column_occurrences
                (TicketId, OccurrenceId, ColumnName, PreviousColumnName, ObservedAt)
            VALUES(@ticket,@occurrence,@column,@previous,@now)
            """;
        record.Parameters.AddWithValue("@ticket", ticketId);
        record.Parameters.AddWithValue("@occurrence", occurrenceId);
        record.Parameters.AddWithValue("@column", columnName);
        record.Parameters.AddWithValue("@previous", (object?)previousColumn ?? DBNull.Value);
        record.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await record.ExecuteNonQueryAsync(ct);
        return occurrenceId;
    }

    private static async Task UpsertPresenceAsync(
        SqliteConnection conn, SqliteTransaction tx, int ticketId, string columnName,
        string occurrenceId, CancellationToken ct)
    {
        await using var presence = conn.CreateCommand();
        presence.Transaction = tx;
        presence.CommandText = """
            INSERT INTO automation_column_presence(TicketId, ColumnName, OccurrenceId, ObservedAt)
            VALUES(@ticket,@column,@occurrence,@now)
            ON CONFLICT(TicketId) DO UPDATE SET ColumnName=@column, OccurrenceId=@occurrence, ObservedAt=@now
            WHERE automation_column_presence.ColumnName <> excluded.ColumnName
            """;
        presence.Parameters.AddWithValue("@ticket", ticketId);
        presence.Parameters.AddWithValue("@column", columnName);
        presence.Parameters.AddWithValue("@occurrence", occurrenceId);
        presence.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await presence.ExecuteNonQueryAsync(ct);
    }

    public async Task<AutomationQueueEntry?> ClaimNextAsync(string slug, TimeSpan lease, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return null;
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        EnsureSchema(conn);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        long? id = null;
        await using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT Id FROM automation_queue
                WHERE (Status='Pending' AND (AvailableAt IS NULL OR AvailableAt <= @now))
                   OR (Status='Running' AND LeaseUntil < @now)
                ORDER BY QueueOrder, Id LIMIT 1
                """;
            select.Parameters.AddWithValue("@now", now.ToString("O"));
            var raw = await select.ExecuteScalarAsync(ct);
            if (raw is not null) id = Convert.ToInt64(raw);
        }
        if (id is null) { await tx.CommitAsync(ct); return null; }
        await using (var claim = conn.CreateCommand())
        {
            claim.Transaction = tx;
            claim.CommandText = """
                UPDATE automation_queue SET Status='Running', Attempts=Attempts+1,
                    StartedAt=COALESCE(StartedAt,@now), LeaseUntil=@lease, Reason=NULL WHERE Id=@id
                """;
            claim.Parameters.AddWithValue("@id", id.Value);
            claim.Parameters.AddWithValue("@now", now.ToString("O"));
            claim.Parameters.AddWithValue("@lease", now.Add(lease).ToString("O"));
            await claim.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await GetAsync(conn, id.Value, ct);
    }

    public async Task FinishAsync(string slug, long id, AutomationQueueStatus status, string? reason = null, CancellationToken ct = default)
    {
        if (status is AutomationQueueStatus.Pending or AutomationQueueStatus.Running)
            throw new ArgumentOutOfRangeException(nameof(status));
        var path = _projects.GetProjectDbPath(slug);
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        EnsureSchema(conn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE automation_queue SET Status=@status, FinishedAt=@now, LeaseUntil=NULL, Reason=@reason WHERE Id=@id";
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RequeueAsync(string slug, long id, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        EnsureSchema(conn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_queue
            SET Status='Pending', Attempts=MAX(0, Attempts-1), LeaseUntil=NULL, Reason=NULL
            WHERE Id=@id AND Status='Running'
            """;
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ScheduleRetryAsync(
        string slug, long id, DateTime availableAt, bool resetAttempts, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        EnsureSchema(conn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE automation_queue
            SET Status='Pending', Attempts=CASE WHEN @reset=1 THEN 0 ELSE Attempts END,
                AvailableAt=@available, LeaseUntil=NULL, FinishedAt=NULL, Reason=NULL
            WHERE Id=@id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@reset", resetAttempts ? 1 : 0);
        cmd.Parameters.AddWithValue("@available", availableAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AutomationQueueEntry>> ListForTicketAsync(string slug, int ticketId, CancellationToken ct = default)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return [];
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        EnsureSchema(conn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT q.*,
              (SELECT COUNT(*) FROM automation_queue a WHERE a.Status='Pending' AND a.QueueOrder < q.QueueOrder) AS Ahead
            FROM automation_queue q WHERE q.TicketId=@ticket ORDER BY q.QueueOrder DESC LIMIT 100
            """;
        cmd.Parameters.AddWithValue("@ticket", ticketId);
        var result = new List<AutomationQueueEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    private static async Task<AutomationQueueEntry?> GetAsync(SqliteConnection conn, long id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT q.*, 0 AS Ahead FROM automation_queue q WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static AutomationQueueEntry Read(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("Id")), r.GetInt32(r.GetOrdinal("TicketId")),
        r.GetString(r.GetOrdinal("AutomationId")), r.GetString(r.GetOrdinal("AutomationName")),
        r.GetString(r.GetOrdinal("OccurrenceId")), r.GetInt64(r.GetOrdinal("QueueOrder")),
        Enum.Parse<AutomationQueueStatus>(r.GetString(r.GetOrdinal("Status"))), r.GetInt32(r.GetOrdinal("Attempts")),
        DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        ReadDate(r, "StartedAt"), ReadDate(r, "FinishedAt"), ReadDate(r, "LeaseUntil"),
        ReadDate(r, "AvailableAt"),
        r.IsDBNull(r.GetOrdinal("Reason")) ? null : r.GetString(r.GetOrdinal("Reason")),
        r.GetInt32(r.GetOrdinal("Ahead")), r.GetString(r.GetOrdinal("AutomationSnapshot")));

    private static DateTime? ReadDate(SqliteDataReader r, string name) =>
        r.IsDBNull(r.GetOrdinal(name)) ? null : DateTime.Parse(r.GetString(r.GetOrdinal(name)), null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS automation_column_presence(
              TicketId INTEGER PRIMARY KEY, ColumnName TEXT NOT NULL, OccurrenceId TEXT NOT NULL, ObservedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS automation_column_occurrences(
              TicketId INTEGER NOT NULL, OccurrenceId TEXT NOT NULL, ColumnName TEXT NOT NULL,
              PreviousColumnName TEXT, ObservedAt TEXT NOT NULL, PRIMARY KEY(TicketId, OccurrenceId));
            CREATE TABLE IF NOT EXISTS automation_queue(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, TicketId INTEGER NOT NULL, AutomationId TEXT NOT NULL,
              AutomationName TEXT NOT NULL, OccurrenceId TEXT NOT NULL, QueueOrder INTEGER NOT NULL DEFAULT 0,
              Status TEXT NOT NULL, Attempts INTEGER NOT NULL, CreatedAt TEXT NOT NULL, StartedAt TEXT,
              FinishedAt TEXT, LeaseUntil TEXT, Reason TEXT, AutomationSnapshot TEXT NOT NULL,
              AvailableAt TEXT,
              UNIQUE(TicketId, AutomationId, OccurrenceId));
            CREATE TRIGGER IF NOT EXISTS automation_queue_order AFTER INSERT ON automation_queue
              WHEN NEW.QueueOrder=0 BEGIN UPDATE automation_queue SET QueueOrder=NEW.Id WHERE Id=NEW.Id; END;
            CREATE INDEX IF NOT EXISTS ix_automation_queue_fifo ON automation_queue(Status, QueueOrder);
            CREATE INDEX IF NOT EXISTS ix_automation_queue_ticket ON automation_queue(TicketId, QueueOrder);
            CREATE INDEX IF NOT EXISTS ix_automation_column_transition
              ON automation_column_occurrences(TicketId, PreviousColumnName, ColumnName, ObservedAt);
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "automation_queue", "AvailableAt", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }
}
