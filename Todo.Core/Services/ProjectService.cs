using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Todo.Core.Data;
using Todo.Core.Models;

namespace Todo.Core.Services;

public partial class ProjectService
{
    private readonly string _dataDir;
    private readonly string _registryPath;

    public string DataDir => _dataDir;

    public ProjectService(string dataDir)
    {
        _dataDir = dataDir;
        _registryPath = Path.Combine(dataDir, "registry.db");
        Directory.CreateDirectory(dataDir);
    }

    private static async Task EnsureProjectColumnsAsync(RegistryDbContext db)
    {
        try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN WorkspacePath TEXT NULL"); }
        catch { /* column already exists */ }
    }

    public async Task<List<Project>> ListProjectsAsync()
    {
        await using var db = new RegistryDbContext(_registryPath);
        await db.Database.EnsureCreatedAsync();
        await EnsureProjectColumnsAsync(db);
        return await db.Projects.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Project> CreateProjectAsync(string name)
    {
        var slug = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "project";

        await using var registry = new RegistryDbContext(_registryPath);
        await registry.Database.EnsureCreatedAsync();
        await EnsureProjectColumnsAsync(registry);

        // Ensure unique slug
        var existing = await registry.Projects.AnyAsync(p => p.Slug == slug);
        if (existing)
        {
            var i = 2;
            while (await registry.Projects.AnyAsync(p => p.Slug == $"{slug}-{i}")) i++;
            slug = $"{slug}-{i}";
        }

        var project = new Project { Name = name, Slug = slug };
        registry.Projects.Add(project);
        await registry.SaveChangesAsync();

        // Create the project database
        var projectDbPath = GetProjectDbPath(slug);
        await using var projectDb = new TodoDbContext(projectDbPath);
        await projectDb.Database.EnsureCreatedAsync();

        return project;
    }

    public async Task<Project?> GetProjectAsync(string slug)
    {
        await using var db = new RegistryDbContext(_registryPath);
        await db.Database.EnsureCreatedAsync();
        await EnsureProjectColumnsAsync(db);
        return await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
    }

    public async Task<Project?> UpdateProjectAsync(string slug, string? workspacePath)
    {
        await using var db = new RegistryDbContext(_registryPath);
        await db.Database.EnsureCreatedAsync();
        await EnsureProjectColumnsAsync(db);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
        if (project is null) return null;
        project.WorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim();
        await db.SaveChangesAsync();
        return project;
    }

    public string ResolveWorkspacePath(Project project) =>
        string.IsNullOrWhiteSpace(project.WorkspacePath)
            ? Path.Combine(_dataDir, "projects", project.Slug)
            : project.WorkspacePath;

    public async Task<bool> DeleteProjectAsync(string slug)
    {
        await using var registry = new RegistryDbContext(_registryPath);
        await registry.Database.EnsureCreatedAsync();
        var project = await registry.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
        if (project is null) return false;
        registry.Projects.Remove(project);
        await registry.SaveChangesAsync();

        // Close any pooled connections then delete the project's SQLite files
        var dbPath = GetProjectDbPath(slug);
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
        if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
        return true;
    }

    public string GetProjectDbPath(string slug) => Path.Combine(_dataDir, "projects", $"{slug}.db");

    public TodoDbContext GetProjectDb(string slug)
    {
        var path = GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new TodoDbContext(path);
        db.Database.EnsureCreated();
        return db;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
