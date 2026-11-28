using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Services;

public sealed record AttachmentCleanupResult(int RemovedFiles, long FreedBytes, int DeferredFiles, bool Aborted);

public sealed partial class TicketAttachmentService
{
    public static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(24);
    public Task<AttachmentCleanupResult> CleanupAsync(CancellationToken ct = default)
        => CleanupAsync(DateTime.UtcNow - OrphanGracePeriod, ct);

    internal async Task<AttachmentCleanupResult> CleanupAsync(DateTime olderThanUtc, CancellationToken ct)
    {
        await using var lease = await AcquireLifecycleAsync(ct);
        var directory = Path.GetDirectoryName(BlobPath(new string('0', 32)))!;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return new(0, 0, 0, true);
        var live = new HashSet<string>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        // Finish the entire reference scan before deleting anything. Missing/unreadable
        // project databases fail closed rather than turning live files into apparent orphans.
        try
        {
            foreach (var project in await projects.ListProjectsAsync())
            {
                var path = projects.GetProjectDbPath(project.Slug);
                if (!File.Exists(path)) return new(0, 0, 0, true);
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await connection.OpenAsync(ct);
                await using var table = connection.CreateCommand();
                table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='TicketAttachments'";
                if (await table.ExecuteScalarAsync(ct) is null) continue;
                await using var columns = connection.CreateCommand();
                columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TicketAttachments') WHERE name='DeletedAt'";
                var hasDeletedAt = Convert.ToInt64(await columns.ExecuteScalarAsync(ct)) != 0;
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT a.Id, " + (hasDeletedAt ? "a.DeletedAt" : "NULL")
                    + ", EXISTS(SELECT 1 FROM Tickets t WHERE t.Id=a.TicketId) FROM TicketAttachments a";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetString(0);
                    if (reader.IsDBNull(1) && reader.GetInt64(2) != 0) live.Add(id);
                    else deleted.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        { return new(0, 0, 0, true); }
        var removed = 0;
        long bytes = 0;
        var deferred = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var partial = name.EndsWith(".part", StringComparison.Ordinal);
            var id = partial ? name[..^5] : name;
            if (!Regex.IsMatch(id, "^[a-f0-9]{32}$") || !partial && live.Contains(id)) continue;
            try
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (file.LastWriteTimeUtc > olderThanUtc && (partial || !deleted.Contains(id))) continue;
                var size = file.Length;
                if (TryDeleteBlob(path)) { removed++; bytes += size; }
                else deferred++;
            }
            catch (IOException) { deferred++; }
            catch (UnauthorizedAccessException) { deferred++; }
        }
        return new(removed, bytes, deferred, false);
    }
}
