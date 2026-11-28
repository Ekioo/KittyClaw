using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class TicketAttachmentLifecycleTests
{
    [Fact]
    public async Task Removing_a_file_preserves_edits_to_its_original_discussion_comment()
    {
        using var directory = new TempDir();
        var projects = new ProjectService(directory.Path);
        var project = await projects.CreateProjectAsync("Edited discussion");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "File");
        var files = new TicketAttachmentService(projects);
        using var input = new MemoryStream([1]);
        var file = await files.AddAsync(project.Slug, ticket.Id, "notes.txt", "owner", input, default);
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            var comment = await db.Comments.FindAsync(file!.CommentId);
            comment!.Content = "Owner added important context.";
            await db.SaveChangesAsync();
        }
        Assert.True(await files.DeleteAsync(project.Slug, ticket.Id, file!.Id, "owner", default));
        await using var check = projects.GetProjectDb(project.Slug);
        Assert.Equal("Owner added important context.", (await check.Comments.FindAsync(file.CommentId))!.Content);
    }
    [Fact]
    public async Task Cleanup_keeps_live_and_recent_files_and_collects_crash_and_deleted_ticket_orphans()
    {
        using var directory = new TempDir();
        var projects = new ProjectService(directory.Path);
        var project = await projects.CreateProjectAsync("Files");
        var tickets = new TicketService(projects, new MemberService(projects));
        var first = await tickets.CreateTicketAsync(project.Slug, "Keep");
        var second = await tickets.CreateTicketAsync(project.Slug, "Delete");
        var files = new TicketAttachmentService(projects);
        using var input = new MemoryStream([1, 2, 3]);
        var live = await files.AddAsync(project.Slug, first.Id, "keep.txt", "owner", input, default);
        using var otherInput = new MemoryStream([4, 5]);
        var removed = await files.AddAsync(project.Slug, second.Id, "remove.txt", "owner", otherInput, default);
        await tickets.DeleteTicketAsync(project.Slug, second.Id);
        var orphan = files.BlobPath(Guid.NewGuid().ToString("N"));
        var partial = files.BlobPath(Guid.NewGuid().ToString("N")) + ".part";
        var fresh = files.BlobPath(Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(orphan, "orphan"); await File.WriteAllTextAsync(partial, "partial"); await File.WriteAllTextAsync(fresh, "fresh");
        foreach (var path in new[] { files.BlobPath(live!.Id), files.BlobPath(removed!.Id), orphan, partial })
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var result = await files.CleanupAsync();
        Assert.False(result.Aborted); Assert.Equal(3, result.RemovedFiles);
        Assert.True(File.Exists(files.BlobPath(live.Id))); Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(orphan)); Assert.False(File.Exists(partial)); Assert.False(File.Exists(files.BlobPath(removed.Id)));
        Assert.Equal(0, (await files.CleanupAsync()).RemovedFiles);
    }

    [Fact]
    public async Task Unreadable_project_reference_scan_prevents_any_collection()
    {
        using var directory = new TempDir();
        var projects = new ProjectService(directory.Path);
        var project = await projects.CreateProjectAsync("Missing database");
        var files = new TicketAttachmentService(projects);
        await using (await files.AcquireLifecycleAsync(default)) { }
        var orphan = files.BlobPath(Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(orphan, "old bytes"); File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Move(projects.GetProjectDbPath(project.Slug), projects.GetProjectDbPath(project.Slug) + ".offline");
        var result = await files.CleanupAsync();
        Assert.True(result.Aborted); Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task Legacy_attachment_schema_is_migrated_and_cleanup_waits_for_upload_finalization()
    {
        using var directory = new TempDir();
        var projects = new ProjectService(directory.Path);
        var project = await projects.CreateProjectAsync("Legacy");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Legacy upload");
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE TicketAttachments");
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE TicketAttachments (Id TEXT PRIMARY KEY, TicketId INTEGER NOT NULL, FileName TEXT NOT NULL, Size INTEGER NOT NULL, Author TEXT NOT NULL, CreatedAt TEXT NOT NULL)");
        }
        var files = new TicketAttachmentService(projects);
        using var input = new MemoryStream([1, 2, 3]);
        var attachment = await files.AddAsync(project.Slug, ticket.Id, "legacy.txt", "owner", input, default, "legacy-key");
        Assert.NotNull(attachment);
        Task<AttachmentCleanupResult> cleanup;
        await using (await files.AcquireLifecycleAsync(default))
        {
            cleanup = files.CleanupAsync(DateTime.UtcNow.AddDays(1), default);
            Assert.False(cleanup.IsCompleted);
        }
        Assert.Equal(0, (await cleanup).RemovedFiles);
        using var repeated = new MemoryStream([1, 2, 3]);
        var replay = await files.AddAsync(project.Slug, ticket.Id, "legacy.txt", "owner", repeated, default, "legacy-key");
        Assert.Equal(attachment.Id, replay!.Id); Assert.True(replay.Replayed);
    }
}
