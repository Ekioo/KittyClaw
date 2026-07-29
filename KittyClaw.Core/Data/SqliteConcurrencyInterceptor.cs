using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KittyClaw.Core.Data;

/// <summary>
/// Applies SQLite concurrency pragmas on every new EF connection:
/// - <c>busy_timeout = 5000</c> — a locked database waits up to 5 s for the lock to clear
///   instead of failing immediately with SQLITE_BUSY ("database is locked"). The engine tick,
///   parallel agent runs and the Blazor UI all hit the same per-project file concurrently, so
///   the default fail-fast behavior produced rare, unreproducible failures on busy boards.
/// - <c>journal_mode = WAL</c> — readers no longer block behind a writer (and vice versa).
///   WAL is persisted in the database file itself, so raw SqliteConnection users
///   (TriggerStateStore, DashboardService, …) benefit as soon as any EF connection has
///   touched the file. Note: WAL adds `-wal`/`-shm` sidecar files next to each `.db`.
/// </summary>
public sealed class SqliteConcurrencyInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteConcurrencyInterceptor Instance = new();

    private const string Pragmas = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is not SqliteConnection sqlite) return;
        using var cmd = sqlite.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection sqlite) return;
        using var cmd = sqlite.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
