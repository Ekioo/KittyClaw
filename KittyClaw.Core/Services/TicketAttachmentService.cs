using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

public sealed partial class TicketAttachmentService(ProjectService projects)
{
    public const long MaximumBytes = 100 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static string DownloadUrl(string slug, int ticketId, string id)
        => $"/api/projects/{slug}/tickets/{ticketId}/attachments/{id}";

    internal static Task EnsureSchemaAsync(TodoDbContext db, CancellationToken ct = default)
        => MigrationGate.RunOnceAsync(db, "TicketAttachments-v2", async context =>
        {
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS TicketAttachments (
                    Id TEXT NOT NULL PRIMARY KEY, TicketId INTEGER NOT NULL,
                    FileName TEXT NOT NULL, Size INTEGER NOT NULL, Author TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                    FOREIGN KEY (TicketId) REFERENCES Tickets(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_TicketAttachments_TicketId ON TicketAttachments(TicketId);
                """);
            foreach (var column in new[] { "Sha256 TEXT NOT NULL DEFAULT ''", "IdempotencyKeyHash TEXT NULL", "CommentId INTEGER NULL", "DeletedAt TEXT NULL" })
                await MigrationGate.AddColumnIfMissingAsync(context, "ALTER TABLE TicketAttachments ADD COLUMN " + column);
            await context.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_TicketAttachments_TicketId_IdempotencyKeyHash ON TicketAttachments(TicketId, IdempotencyKeyHash)");
        });

    // Serializes upload finalization, deletion, transfer and GC across processes sharing this data directory.
    internal async Task<IAsyncDisposable> AcquireLifecycleAsync(CancellationToken ct)
    {
        var directory = Path.GetFullPath(Path.Combine(projects.DataDir, "attachments"));
        var gate = Gates.GetOrAdd(directory, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { return new LifecycleLease(gate, new FileStream(Path.Combine(directory, ".maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
                { await Task.Delay(100, ct); }
            }
        }
        catch { gate.Release(); throw; }
    }

    private sealed class LifecycleLease(SemaphoreSlim gate, FileStream file) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { file.Dispose(); gate.Release(); return ValueTask.CompletedTask; }
    }

    public async Task<TicketAttachment?> AddAsync(string slug, int ticketId, string fileName,
        string author, Stream input, CancellationToken ct, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(author) || author.Length > 120 || author.Any(char.IsControl))
            throw new ArgumentException("A valid author is required.");
        fileName = fileName.Replace('\\', '/').Split('/').Last().Trim();
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.Length > 240 || fileName.Any(char.IsControl))
            throw new ArgumentException("A valid file name is required.");
        if (idempotencyKey is not null && !Regex.IsMatch(idempotencyKey, "^[A-Za-z0-9._:-]{1,128}$"))
            throw new ArgumentException("Idempotency-Key must contain 1-128 ASCII letters, digits, dots, underscores, colons or hyphens.");
        await using var lease = await AcquireLifecycleAsync(ct);
        if (await projects.GetProjectAsync(slug) is null) return null;
        await using var db = projects.GetProjectDb(slug);
        await EnsureSchemaAsync(db, ct);
        var ticket = await db.Tickets.FindAsync([ticketId], ct);
        if (ticket is null) return null;
        var attachment = new TicketAttachment { Id = Guid.NewGuid().ToString("N"), TicketId = ticketId, FileName = fileName, Author = author.Trim(),
            IdempotencyKeyHash = idempotencyKey is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))) };
        var staging = BlobPath(attachment.Id) + ".part";
        var final = BlobPath(attachment.Id);
        var finalCreated = false;
        var committed = false;
        var commitAttempted = false;
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int length;
                while ((length = await input.ReadAsync(buffer, ct)) > 0)
                {
                    attachment.Size += length;
                    if (attachment.Size > MaximumBytes) throw new AttachmentTooLargeException();
                    digest.AppendData(buffer, 0, length);
                    await output.WriteAsync(buffer.AsMemory(0, length), ct);
                }
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            if (attachment.Size == 0) throw new ArgumentException("The file is empty.");
            attachment.Sha256 = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (attachment.IdempotencyKeyHash is not null)
            {
                var existing = await db.TicketAttachments.FirstOrDefaultAsync(a => a.TicketId == ticketId && a.IdempotencyKeyHash == attachment.IdempotencyKeyHash, ct);
                if (existing is not null)
                {
                    if (existing.FileName != attachment.FileName || existing.Author != attachment.Author || existing.Size != attachment.Size || existing.Sha256 != attachment.Sha256)
                        throw new AttachmentIdempotencyConflictException();
                    if (existing.DeletedAt is not null) throw new AttachmentGoneException();
                    if (!File.Exists(BlobPath(existing.Id))) throw new IOException("The recorded attachment file is unavailable.");
                    existing.Replayed = true;
                    return existing;
                }
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var comment = new Comment { TicketId = ticketId, Author = attachment.Author,
                Content = AttachmentComment(slug, attachment) };
            db.Comments.Add(comment);
            ticket.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            attachment.CommentId = comment.Id;
            db.TicketAttachments.Add(attachment);
            await db.SaveChangesAsync(ct);
            File.Move(staging, final);
            finalCreated = true;
            // An uncertain commit must retain the blob; GC later resolves metadata versus bytes.
            commitAttempted = true;
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
            return attachment;
        }
        finally
        {
            File.Delete(staging);
            if (finalCreated && !committed && !commitAttempted) File.Delete(final);
        }
    }

    public async Task<List<TicketAttachment>?> ListAsync(string slug, int ticketId, CancellationToken ct)
    {
        if (await projects.GetProjectAsync(slug) is null) return null;
        await using var db = projects.GetProjectDb(slug);
        await EnsureSchemaAsync(db, ct);
        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId, ct)) return null;
        return await db.TicketAttachments.Where(a => a.TicketId == ticketId && a.DeletedAt == null).OrderBy(a => a.CreatedAt).ToListAsync(ct);
    }

    public async Task<TicketAttachment?> FindAsync(string slug, int ticketId, string id, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$") || await projects.GetProjectAsync(slug) is null) return null;
        await using var db = projects.GetProjectDb(slug);
        await EnsureSchemaAsync(db, ct);
        return await db.TicketAttachments.FirstOrDefaultAsync(a => a.Id == id && a.TicketId == ticketId && a.DeletedAt == null && db.Tickets.Any(t => t.Id == ticketId), ct);
    }

    public async Task<bool> DeleteAsync(string slug, int ticketId, string id, string author, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(author) || author.Length > 120 || author.Any(char.IsControl)) throw new ArgumentException("A valid author is required.");
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$") || await projects.GetProjectAsync(slug) is null) return false;
        await using var lease = await AcquireLifecycleAsync(ct);
        await using var db = projects.GetProjectDb(slug);
        await EnsureSchemaAsync(db, ct);
        var attachment = await db.TicketAttachments.FirstOrDefaultAsync(a => a.Id == id && a.TicketId == ticketId && db.Tickets.Any(t => t.Id == ticketId), ct);
        if (attachment is null) return false;
        if (attachment.DeletedAt is null)
        {
            await TicketService.EnsureActivityTableAsync(db);
            attachment.DeletedAt = DateTime.UtcNow;
            var comment = attachment.CommentId is int commentId ? await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.TicketId == ticketId, ct) : null;
            if (comment is null && attachment.CommentId is null)
            {
                var url = DownloadUrl(slug, ticketId, id);
                comment = await db.Comments.FirstOrDefaultAsync(c => c.TicketId == ticketId && c.Author == attachment.Author
                    && c.Content.StartsWith("Attachment: [") && c.Content.EndsWith("](" + url + ")"), ct);
            }
            if (comment is not null && comment.Content == AttachmentComment(slug, attachment)) comment.Content = "Attachment removed.";
            var ticket = await db.Tickets.FindAsync([ticketId], ct);
            if (ticket is null) return false;
            ticket.UpdatedAt = DateTime.UtcNow;
            db.ActivityEntries.Add(new ActivityEntry { TicketId = ticketId, Author = author.Trim(), Text = "Removed attachment " + attachment.FileName });
            await db.SaveChangesAsync(ct);
        }
        TryDeleteBlob(BlobPath(id));
        return true;
    }

    private static bool TryDeleteBlob(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string AttachmentComment(string slug, TicketAttachment attachment)
    {
        var label = new StringBuilder();
        foreach (var rune in attachment.FileName.EnumerateRunes()) label.Append($"&#{rune.Value};");
        return $"Attachment: [{label}]({DownloadUrl(slug, attachment.TicketId, attachment.Id)})";
    }

    public string BlobPath(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new ArgumentException("Invalid attachment ID.");
        return Path.Combine(projects.DataDir, "attachments", id);
    }
}

public sealed class AttachmentTooLargeException : Exception;
public sealed class AttachmentIdempotencyConflictException : Exception;
public sealed class AttachmentGoneException : Exception;
