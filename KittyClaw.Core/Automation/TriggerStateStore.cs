using Microsoft.Data.Sqlite;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public interface ITriggerStateStore
{
    Task<DateTime?> GetLastRunAtAsync(string slug, string automationId);
    Task SetLastRunAtAsync(string slug, string automationId, DateTime lastRunAt);
}

/// <summary>
/// Persists the last-run timestamp for interval/cron triggers per project automation.
/// Stored in the per-project SQLite DB so it survives restarts.
/// </summary>
public sealed class TriggerStateStore : ITriggerStateStore
{
    private readonly ProjectService _projects;

    public TriggerStateStore(ProjectService projects)
    {
        _projects = projects;
    }

    public async Task<DateTime?> GetLastRunAtAsync(string slug, string automationId)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return null;
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        EnsureTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LastRunAt FROM automation_trigger_state WHERE AutomationId = @id";
        cmd.Parameters.AddWithValue("@id", automationId);
        var raw = await cmd.ExecuteScalarAsync() as string;
        if (raw is null) return null;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;
    }

    public async Task SetLastRunAtAsync(string slug, string automationId, DateTime lastRunAt)
    {
        var path = _projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        EnsureTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO automation_trigger_state (AutomationId, LastRunAt)
            VALUES (@id, @ts)
            ON CONFLICT(AutomationId) DO UPDATE SET LastRunAt = @ts
            """;
        cmd.Parameters.AddWithValue("@id", automationId);
        cmd.Parameters.AddWithValue("@ts", lastRunAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS automation_trigger_state (
                AutomationId TEXT NOT NULL PRIMARY KEY,
                LastRunAt TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }
}
