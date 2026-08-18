using System.Collections.Concurrent;
using System.Diagnostics;
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
    private static readonly TimeSpan RepositoryResolutionCacheDuration = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, RepositoryPathCacheEntry> _repositoryPathCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _repositoryPathLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _dbInitialized;

    private sealed record RepositoryPathCacheEntry(string ResolvedPath, DateTime ExpiresAt);

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
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN WorktreesEnabled INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already exists */ }
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN IntegrationBranch TEXT NULL"); }
            catch { /* column already exists */ }
            try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN RepositoryPath TEXT NULL"); }
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
        var projects = await db.Projects.OrderBy(p => p.Name).ToListAsync();
        foreach (var project in projects) PopulateResolvedRepositoryPath(project);
        return projects;
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
        // Script actions need a real working directory even before the user assigns an
        // external workspace to a newly created project.
        Directory.CreateDirectory(ResolveWorkspacePath(project));
        await using var projectDb = GetProjectDb(slug);

        return project;
    }

    public async Task<Project?> GetProjectAsync(string slug)
    {
        await EnsureRegistryInitializedAsync();
        await using var db = new RegistryDbContext(_registryPath);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
        if (project is not null) PopulateResolvedRepositoryPath(project);
        return project;
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

    // Roots where agents must never run or write: KittyClaw creates .agents/** in the
    // workspace and launches permission-less claude subprocesses there, so a workspace
    // inside a system directory turns any typo (or confused agent calling the open API)
    // into an incident. Validated at write time only — existing projects are untouched.
    private static readonly string[] ForbiddenUnixRoots =
        ["/etc", "/usr", "/bin", "/sbin", "/lib", "/lib64", "/boot", "/proc", "/sys", "/dev", "/System"];

    /// <summary>
    /// Rejects workspace paths that are relative, contain "..", point at a filesystem
    /// root, or live under a system directory (backport analysis §2.6). Throws
    /// <see cref="InvalidOperationException"/> with an actionable message (mapped to 400).
    /// </summary>
    public static void ValidateWorkspacePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"Le workspace doit être un chemin absolu ('{path}' ne l'est pas).");
        if (path.Split('/', '\\').Any(segment => segment == ".."))
            throw new InvalidOperationException("Le workspace ne doit pas contenir de segment '..'.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full, root, comparison))
            throw new InvalidOperationException("Le workspace ne peut pas être la racine d'un disque.");

        var forbidden = OperatingSystem.IsWindows()
            ? new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            : ForbiddenUnixRoots;
        foreach (var dir in forbidden)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var f = Path.TrimEndingDirectorySeparator(dir);
            if (string.Equals(full, f, comparison) || full.StartsWith(f + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException($"Le workspace ne peut pas être dans '{f}' (répertoire système).");
        }
    }

    public async Task<Project?> UpdateProjectAsync(
        string slug,
        string? workspacePath,
        string? fallbackModel = null,
        bool updateFallback = false,
        bool? worktreesEnabled = null,
        string? integrationBranch = null,
        string? repositoryPath = null)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
            ValidateWorkspacePath(workspacePath.Trim());
        await EnsureRegistryInitializedAsync();
        await using var db = new RegistryDbContext(_registryPath);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug);
        if (project is null) return null;

        // A worktree-only PATCH must not clear the workspace merely because WorkspacePath
        // was omitted from JSON. Preserve the historical clear behavior for ordinary patches.
        var updatesWorktreeSettings = worktreesEnabled.HasValue || integrationBranch is not null || repositoryPath is not null;
        if (!updatesWorktreeSettings || workspacePath is not null)
            project.WorkspacePath = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim();

        var normalizedBranch = integrationBranch?.Trim();
        var resultingBranch = integrationBranch is null ? project.IntegrationBranch : normalizedBranch;
        var resultingEnabled = worktreesEnabled ?? project.WorktreesEnabled;
        if (repositoryPath is not null)
            project.RepositoryPath = NormalizeRepositoryPath(project, repositoryPath);
        if (resultingEnabled)
        {
            var repositoryCandidate = string.IsNullOrWhiteSpace(project.RepositoryPath)
                ? ResolveWorkspacePath(project)
                : project.RepositoryPath;
            ValidateWorktreeConfiguration(repositoryCandidate, resultingBranch,
                explicitlyConfigured: !string.IsNullOrWhiteSpace(project.RepositoryPath));
        }

        if (integrationBranch is not null)
            project.IntegrationBranch = string.IsNullOrWhiteSpace(normalizedBranch) ? null : normalizedBranch;
        if (worktreesEnabled.HasValue)
            project.WorktreesEnabled = worktreesEnabled.Value;
        if (updateFallback)
        {
            project.FallbackModel = string.IsNullOrWhiteSpace(fallbackModel) ? null : fallbackModel.Trim();
        }
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        PopulateResolvedRepositoryPath(project);
        return project;
    }

    private string? NormalizeRepositoryPath(Project project, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var resolved = Path.IsPathFullyQualified(trimmed)
            ? trimmed
            : Path.Combine(ResolveWorkspacePath(project), trimmed);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }

    private static void ValidateWorktreeConfiguration(string repositoryPath, string? integrationBranch, bool explicitlyConfigured)
    {
        if (string.IsNullOrWhiteSpace(integrationBranch))
            throw new InvalidOperationException("La branche d’intégration est requise pour activer les worktrees.");
        if (!Directory.Exists(repositoryPath))
            throw new InvalidOperationException($"Le dépôt de code '{repositoryPath}' n’existe pas.");

        var repository = RunGit(repositoryPath, ["rev-parse", "--is-inside-work-tree"]);
        if (!repository.Success || !string.Equals(repository.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Le dépôt de code '{repositoryPath}' n’est pas un dépôt Git utilisable.");

        var topLevel = RunGit(repositoryPath, ["rev-parse", "--show-toplevel"]);
        var resolvedRoot = topLevel.Success ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLevel.Output.Trim())) : "";
        if (explicitlyConfigured && !PathsEqual(repositoryPath, resolvedRoot))
            throw new InvalidOperationException($"Le dépôt configuré '{repositoryPath}' n’est pas une racine Git. Git a résolu '{resolvedRoot}'. Configurez la racine exacte du dépôt de code.");

        var worktreeSupport = RunGit(resolvedRoot, ["worktree", "list", "--porcelain"]);
        if (!worktreeSupport.Success)
            throw new InvalidOperationException("Cette version de Git ne prend pas en charge les worktrees.");

        var validBranch = RunGit(resolvedRoot, ["check-ref-format", "--branch", integrationBranch]);
        if (!validBranch.Success)
            throw new InvalidOperationException($"La branche d’intégration '{integrationBranch}' est invalide.");

        var branchExists = RunGit(resolvedRoot, ["show-ref", "--verify", "--quiet", $"refs/heads/{integrationBranch}"]);
        if (!branchExists.Success)
            throw new InvalidOperationException($"La branche d’intégration '{integrationBranch}' n’existe pas dans le dépôt.");
    }

    private static (bool Success, string Output) RunGit(string workspacePath, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, "");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return (false, output);
            }
            return (process.ExitCode == 0, output);
        }
        catch
        {
            return (false, "");
        }
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

    public string ResolveRepositoryPath(Project project)
    {
        var candidate = string.IsNullOrWhiteSpace(project.RepositoryPath)
            ? ResolveWorkspacePath(project)
            : project.RepositoryPath;
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (!Directory.Exists(normalizedCandidate)) return normalizedCandidate;

        var now = DateTime.UtcNow;
        if (_repositoryPathCache.TryGetValue(normalizedCandidate, out var cached) && cached.ExpiresAt > now)
            return cached.ResolvedPath;

        var resolutionLock = _repositoryPathLocks.GetOrAdd(normalizedCandidate, static _ => new object());
        lock (resolutionLock)
        {
            now = DateTime.UtcNow;
            if (_repositoryPathCache.TryGetValue(normalizedCandidate, out cached) && cached.ExpiresAt > now)
                return cached.ResolvedPath;

            var result = RunGit(normalizedCandidate, ["rev-parse", "--show-toplevel"]);
            var resolved = result.Success && !string.IsNullOrWhiteSpace(result.Output)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.Output.Trim()))
                : normalizedCandidate;
            _repositoryPathCache[normalizedCandidate] =
                new RepositoryPathCacheEntry(resolved, now + RepositoryResolutionCacheDuration);
            return resolved;
        }
    }

    private void PopulateResolvedRepositoryPath(Project project) =>
        project.ResolvedRepositoryPath = ResolveRepositoryPath(project);

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

    private static readonly ConcurrentDictionary<string, Lazy<bool>> _schemaCreated = new(StringComparer.OrdinalIgnoreCase);

    public TodoDbContext GetProjectDb(string slug)
    {
        var path = GetProjectDbPath(slug);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new TodoDbContext(path);
        // Publish a completion gate, not merely an "initialization started" marker. A project
        // becomes visible in the registry just before its database is created, so background
        // services can reach this method concurrently with CreateProjectAsync.
        var schema = _schemaCreated.GetOrAdd(path, static dbPath => new Lazy<bool>(() =>
        {
            using var initializer = new TodoDbContext(dbPath);
            return initializer.Database.EnsureCreated();
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            _ = schema.Value;
        }
        catch
        {
            _schemaCreated.TryRemove(new KeyValuePair<string, Lazy<bool>>(path, schema));
            db.Dispose();
            throw;
        }
        return db;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ValidSlugRegex();
}
