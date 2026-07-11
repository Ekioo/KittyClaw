using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Data;

/// <summary>
/// Memoizes the inline CREATE TABLE / ALTER TABLE migrations so each runs once per
/// database file instead of on every service call (ListTicketsAsync alone chained ~10
/// DDL round-trips per request). A failed migration (locked db, disk full) stays failed
/// for that call — the error surfaces to the caller — and is retried on the next call.
/// </summary>
public static class MigrationGate
{
    private static readonly ConcurrentDictionary<string, Task> _done = new(StringComparer.OrdinalIgnoreCase);

    public static Task RunOnceAsync(TodoDbContext db, string migrationKey, Func<TodoDbContext, Task> ddl) =>
        RunOnceCoreAsync($"{db.DbPath}::{migrationKey}", () => ddl(db));

    public static Task RunOnceAsync(string dbPath, string migrationKey, Func<Task> ddl) =>
        RunOnceCoreAsync($"{dbPath}::{migrationKey}", ddl);

    private static async Task RunOnceCoreAsync(string key, Func<Task> ddl)
    {
        var task = _done.GetOrAdd(key, _ => ddl());
        if (task.IsFaulted || task.IsCanceled)
        {
            // Previous attempt failed (e.g. transient SQLITE_BUSY) — retry with this caller's
            // context. The DDL is idempotent, so a rare double-run under race is harmless.
            task = ddl();
            _done[key] = task;
        }
        await task.ConfigureAwait(false);
    }

    /// <summary>ALTER TABLE ADD COLUMN that tolerates the column already existing but surfaces every other failure.</summary>
    public static async Task AddColumnIfMissingAsync(TodoDbContext db, string alterSql)
    {
        try { await db.Database.ExecuteSqlRawAsync(alterSql); }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists — expected on already-migrated databases.
        }
    }

    /// <summary>Forgets everything memoized for a database file (call when the file is deleted).</summary>
    public static void Invalidate(string dbPath)
    {
        var prefix = dbPath + "::";
        foreach (var key in _done.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _done.TryRemove(key, out _);
    }
}
