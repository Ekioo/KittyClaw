using Microsoft.Data.Sqlite;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public interface ITriggerStateStore
{
    Task<DateTime?> GetNextRunAtAsync(string slug, string automationId);
    Task SetNextRunAtAsync(string slug, string automationId, DateTime nextRunAt);

    /// <summary>
    /// Reads the legacy LastRunAt value left over from before the NextRunAt model, if any.
    /// Used once as a migration baseline: a pre-existing install's row has NextRunAt = NULL after
    /// the schema migration, and without this, the automation would be seeded from "now" — silently
    /// dropping a legitimately missed occurrence instead of catching up, for every automation that
    /// existed before the upgrade. Returns null for automations that never ran under the old model.
    /// </summary>
    Task<DateTime?> GetLegacyLastRunAtAsync(string slug, string automationId);
}

/// <summary>
/// Persists the next scheduled fire time for each cron/interval automation, per project.
/// Stored in the per-project SQLite DB so it survives restarts: the fire time is computed once at
/// registration and saved immediately, so a restart that straddles the scheduled moment still fires
/// on time instead of silently recomputing "next" from the restart time.
/// </summary>
public sealed class TriggerStateStore : ITriggerStateStore
{
    private readonly ProjectService _projects;

    public TriggerStateStore(ProjectService projects)
    {
        _projects = projects;
    }

    public async Task<DateTime?> GetNextRunAtAsync(string slug, string automationId)
    {
        var path = _projects.GetProjectDbPath(slug);
        if (!File.Exists(path)) return null;
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        EnsureTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NextRunAt FROM automation_trigger_state WHERE AutomationId = @id";
        cmd.Parameters.AddWithValue("@id", automationId);
        var raw = await cmd.ExecuteScalarAsync() as string;
        if (raw is null) return null;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;
    }

    public async Task<DateTime?> GetLegacyLastRunAtAsync(string slug, string automationId)
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

    public async Task SetNextRunAtAsync(string slug, string automationId, DateTime nextRunAt)
    {
        var path = _projects.GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        EnsureTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO automation_trigger_state (AutomationId, NextRunAt, LastRunAt)
            VALUES (@id, @ts, @ts)
            ON CONFLICT(AutomationId) DO UPDATE SET NextRunAt = @ts
            """;
        cmd.Parameters.AddWithValue("@id", automationId);
        cmd.Parameters.AddWithValue("@ts", nextRunAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            // LastRunAt is a dead legacy column kept only because pre-existing installs have it
            // NOT NULL — the INSERT above still writes to it so those old tables don't reject new
            // rows. Nothing reads it anymore; NextRunAt is the only source of truth.
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS automation_trigger_state (
                    AutomationId TEXT NOT NULL PRIMARY KEY,
                    NextRunAt TEXT,
                    LastRunAt TEXT
                )
                """;
            cmd.ExecuteNonQuery();
        }
        // Pre-existing installs had a NOT NULL LastRunAt column and no NextRunAt column.
        // Existing rows migrate to NextRunAt = NULL: their automation is treated as "not yet
        // scheduled" and gets a fresh NextRunAt computed from now on the next reload.
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE automation_trigger_state ADD COLUMN NextRunAt TEXT";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }
    }
}
