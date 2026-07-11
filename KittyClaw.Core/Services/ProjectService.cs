using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Data;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public partial class ProjectService
{
    private readonly string _dataDir;
    private readonly string _registryPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _dbInitialized;

    public string DataDir => _dataDir;

    public ProjectService(string dataDir)
    {
        _dataDir = dataDir;
        _registryPath = Path.Combine(dataDir, "registry.db");
        Directory.CreateDirectory(dataDir);
    }

    private async Task EnsureRegistryInitializedAsync()
    {
        if (_dbInitialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_dbInitialized) return;
            await using var db = new RegistryDbContext(_registryPath);
            await db.Database.EnsureCreatedAsync();
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN WorkspacePath TEXT NULL"); }
            catch { /* column already exists */ }
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN IsPaused INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN FallbackModel TEXT NULL"); }
            catch { /* column already exists */ }
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '1970-01-01 00:00:00'"); }
            catch { /* column already exists */ }
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN LocalModelBaseUrl TEXT NULL"); }
            catch { /* column already exists */ }
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN LocalModelName TEXT NULL"); }
            catch { /* column already exists */ }
            _dbInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<List<Project>> ListProjectsAsync()
    {
        await EnsureRegistryInitializedAsync();
        await using var db = new RegistryDbContext(_registryPath);
        return await db.Projects.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Project> CreateProjectAsync(string name)
    {
        var slug = SlugRegex().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "project";

        await EnsureRegistryInitializedAsync();
        await using var registry = new RegistryDbContext(_registryPath);

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
        Directory.CreateDirectory(Path.GetDirectoryName(projectDbPath)!);
        await using var projectDb = new TodoDbContext(projectDbPath);
        await projectDb.Database.EnsureCreatedAsync();

        return project;
    }

    public async Task<Project?> GetProjectAsync(string slug)
    {
        await EnsureRegistryInitializedAsync();
        await using var db = new RegistryDbContext(_registryPath);
        return await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
    }

    public async Task<Project?> TogglePauseAsync(string slug)
    {
        await EnsureRegistryInitializedAsync();
        await using var db = new RegistryDbContext(_registryPath);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
        if (project is null) return null;
        project.IsPaused = !project.IsPaused;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return project;
    }

    public async Task<Project?> UpdateProjectAsync(string slug, string? workspacePath, string? fallbackModel = null, bool updateFallback = false)
    {
        await EnsureRegistryInitializedAsync();
        await using var db = new RegistryDbContext(_registryPath);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
        if (project is null) return null;
        project.WorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim();
        if (updateFallback)
        {
            project.FallbackModel = string.IsNullOrWhiteSpace(fallbackModel) ? null : fallbackModel.Trim();
        }
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return project;
    }

    public async Task<Project?> SaveLocalModelConfigAsync(string slug, string? baseUrl, string? modelName)
    {
        await EnsureRegistryInitializedAsync();
        await using var db = new RegistryDbContext(_registryPath);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
        if (project is null) return null;
        project.LocalModelBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();
        project.LocalModelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim();
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return project;
    }

    public string ResolveWorkspacePath(Project project) =>
        string.IsNullOrWhiteSpace(project.WorkspacePath)
            ? Path.Combine(_dataDir, "projects", project.Slug)
            : project.WorkspacePath;

    public async Task<bool> DeleteProjectAsync(string slug)
    {
        await EnsureRegistryInitializedAsync();
        await using var registry = new RegistryDbContext(_registryPath);
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
        // A future project reusing this slug starts from a fresh file: forget the memoized
        // schema/migration state so it gets recreated and remigrated.
        _schemaCreated.TryRemove(dbPath, out _);
        KittyClaw.Core.Data.MigrationGate.Invalidate(dbPath);
        return true;
    }

    public string GetProjectDbPath(string slug)
    {
        // Slugs are only ever generated by CreateProjectAsync ([a-z0-9-]); anything else in a
        // route is hostile (e.g. '..\..\x' would place the db file outside the projects dir).
        if (string.IsNullOrEmpty(slug) || !ValidSlugRegex().IsMatch(slug))
            throw new ArgumentException($"Invalid project slug '{slug}'.", nameof(slug));
        return Path.Combine(_dataDir, "projects", $"{slug}.db");
    }

    private static readonly ConcurrentDictionary<string, bool> _schemaCreated = new(StringComparer.OrdinalIgnoreCase);

    public TodoDbContext GetProjectDb(string slug)
    {
        var path = GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new TodoDbContext(path);
        // EnsureCreated is a full schema comparison on every call — run it once per db file.
        // A concurrently deleted file is handled by the Invalidate call in DeleteProjectAsync.
        if (_schemaCreated.TryAdd(path, true))
        {
            try { db.Database.EnsureCreated(); }
            catch { _schemaCreated.TryRemove(path, out _); throw; }
        }
        return db;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ValidSlugRegex();
}
