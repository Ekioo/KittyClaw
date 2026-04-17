using Microsoft.EntityFrameworkCore;
using Todo.Core.Data;
using Todo.Core.Models;

namespace Todo.Core.Services;

public class MemberService
{
    private readonly ProjectService _projectService;

    public MemberService(ProjectService projectService)
    {
        _projectService = projectService;
    }

    private static async Task EnsureMemberTableAsync(TodoDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Members (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Slug TEXT NOT NULL DEFAULT '',
                IsAgent INTEGER NOT NULL DEFAULT 0
            )
        """);
        // Add Slug column if missing (migration for existing DBs)
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Members ADD COLUMN Slug TEXT NOT NULL DEFAULT ''"); }
        catch { /* column already exists */ }
        // Old Skill column, kept around for DBs that have it (unused now).
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Members ADD COLUMN Skill TEXT NULL"); }
        catch { /* column already exists */ }
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Members ADD COLUMN IsAgent INTEGER NOT NULL DEFAULT 0"); }
        catch { /* column already exists */ }
    }

    public async Task<List<Member>> ListMembersAsync(string projectSlug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureMemberTableAsync(db);
        await BackfillSlugsAsync(db);
        return await db.Members.OrderBy(m => m.Name).ToListAsync();
    }

    public async Task<bool> MemberExistsAsync(string projectSlug, string slug)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureMemberTableAsync(db);
        await BackfillSlugsAsync(db);
        return await db.Members.AnyAsync(m => m.Slug == slug);
    }

    /// <summary>
    /// Backfill slugs for members that were created before the Slug column existed.
    /// </summary>
    private static async Task BackfillSlugsAsync(TodoDbContext db)
    {
        var needsSlug = await db.Members.Where(m => m.Slug == "").ToListAsync();
        if (needsSlug.Count == 0) return;
        foreach (var m in needsSlug)
            m.Slug = Member.ToSlug(m.Name);
        await db.SaveChangesAsync();
    }

    public async Task<Member> CreateMemberAsync(string projectSlug, string name)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureMemberTableAsync(db);
        await BackfillSlugsAsync(db);
        var member = new Member { Name = name, Slug = Member.ToSlug(name) };
        db.Members.Add(member);
        await db.SaveChangesAsync();
        return member;
    }

    public async Task<Member?> UpdateMemberAsync(string projectSlug, int memberId, string? name = null)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureMemberTableAsync(db);
        var member = await db.Members.FindAsync(memberId);
        if (member is null) return null;
        if (name is not null)
        {
            member.Name = name;
            member.Slug = Member.ToSlug(name);
        }
        await db.SaveChangesAsync();
        return member;
    }

    public async Task<bool> DeleteMemberAsync(string projectSlug, int memberId)
    {
        await using var db = _projectService.GetProjectDb(projectSlug);
        await EnsureMemberTableAsync(db);
        var member = await db.Members.FindAsync(memberId);
        if (member is null) return false;
        db.Members.Remove(member);
        await db.SaveChangesAsync();
        return true;
    }
}
