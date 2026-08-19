using System.Diagnostics;
using Microsoft.Data.Sqlite;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProjectWorktreeSettingsTests
{
    [Fact]
    public async Task NewProject_DefaultsToWorktreesEnabled_AndCanBeDisabledExplicitly()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);

        var project = await projects.CreateProjectAsync("new-default");

        Assert.True(project.WorktreesEnabled);

        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        var loaded = await projects.GetProjectAsync(project.Slug);

        Assert.NotNull(loaded);
        Assert.False(loaded.WorktreesEnabled);
    }

    [Fact]
    public async Task ExistingRegistry_MigratesWithWorktreesDisabled()
    {
        using var temp = new TempDir();
        var registryPath = Path.Combine(temp.Path, "registry.db");
        await using (var connection = new SqliteConnection($"Data Source={registryPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Projects (
                    Id INTEGER NOT NULL CONSTRAINT PK_Projects PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL, Slug TEXT NOT NULL, CreatedAt TEXT NOT NULL
                );
                INSERT INTO Projects (Name, Slug, CreatedAt)
                VALUES ('Legacy', 'legacy', '2026-01-01 00:00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var project = await new ProjectService(temp.Path).GetProjectAsync("legacy");

        Assert.NotNull(project);
        Assert.False(project.WorktreesEnabled);
        Assert.Null(project.IntegrationBranch);
        Assert.Null(project.RepositoryPath);
    }

    [Fact]
    public async Task ExplicitNestedRepository_IsResolvedRelativeToWorkspaceAndPersistedAbsolutely()
    {
        using var temp = new TempDir();
        var workspace = CreateRepository(temp.Path, "outer");
        var nested = CreateRepository(workspace, "integration");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("nested-repository");
        await projects.UpdateProjectAsync(project.Slug, workspace);

        var enabled = await projects.UpdateProjectAsync(project.Slug, null,
            worktreesEnabled: true, integrationBranch: "integration",
            repositoryPath: Path.GetRelativePath(workspace, nested));

        Assert.Equal(Path.GetFullPath(nested), enabled!.RepositoryPath, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(nested), enabled.ResolvedRepositoryPath, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(nested), projects.ResolveRepositoryPath(enabled), ignoreCase: true);
    }

    [Fact]
    public async Task ExplicitPathInsideRepository_IsRejectedInsteadOfSelectingParent()
    {
        using var temp = new TempDir();
        var repository = CreateRepository(temp.Path, "integration");
        var child = Path.Combine(repository, "child");
        Directory.CreateDirectory(child);
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("wrong-root");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        await projects.UpdateProjectAsync(project.Slug, repository);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => projects.UpdateProjectAsync(
            project.Slug, null, worktreesEnabled: true, integrationBranch: "integration", repositoryPath: child));

        Assert.Contains("racine Git", error.Message);
        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.False(loaded!.WorktreesEnabled);
        Assert.Null(loaded.RepositoryPath);
    }

    [Fact]
    public async Task EnableAndDisable_PersistWithoutOverwritingOtherSettings()
    {
        using var temp = new TempDir();
        var repository = CreateRepository(temp.Path, "integration");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("worktree-settings");
        await projects.UpdateProjectAsync(project.Slug, repository, fallbackModel: "grok-4.5", updateFallback: true);

        var enabled = await projects.UpdateProjectAsync(
            project.Slug, workspacePath: null, worktreesEnabled: true, integrationBranch: "integration");

        Assert.True(enabled!.WorktreesEnabled);
        Assert.Equal("integration", enabled.IntegrationBranch);
        Assert.Equal(repository, enabled.WorkspacePath);
        Assert.Equal("grok-4.5", enabled.FallbackModel);

        var disabled = await projects.UpdateProjectAsync(
            project.Slug, workspacePath: null, worktreesEnabled: false);
        Assert.False(disabled!.WorktreesEnabled);
        Assert.Equal("integration", disabled.IntegrationBranch);
        Assert.Equal(repository, disabled.WorkspacePath);
    }

    [Fact]
    public async Task Enable_RejectsNonGitWorkspaceWithoutPersistingSettings()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "not-git");
        Directory.CreateDirectory(workspace);
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("non-git");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        await projects.UpdateProjectAsync(project.Slug, workspace);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true, integrationBranch: "main"));

        Assert.Contains("dépôt Git", error.Message);
        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.False(loaded!.WorktreesEnabled);
        Assert.Null(loaded.IntegrationBranch);
        Assert.Equal(workspace, loaded.WorkspacePath);
    }

    [Fact]
    public async Task Enable_RejectsMissingBranchWithoutPersistingSettings()
    {
        using var temp = new TempDir();
        var repository = CreateRepository(temp.Path, "main");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("missing-branch");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        await projects.UpdateProjectAsync(project.Slug, repository);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true, integrationBranch: "absent"));

        Assert.Contains("n’existe pas", error.Message);
        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.False(loaded!.WorktreesEnabled);
        Assert.Null(loaded.IntegrationBranch);
    }

    [Fact]
    public void RepositoryResolution_IsReusedDuringTheShortCacheWindow()
    {
        using var temp = new TempDir();
        var repository = CreateRepository(temp.Path, "main");
        var nestedWorkspace = Path.Combine(repository, "workspace");
        Directory.CreateDirectory(nestedWorkspace);
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = new KittyClaw.Core.Models.Project
        {
            Name = "Cached repository",
            Slug = "cached-repository",
            WorkspacePath = nestedWorkspace,
        };

        var first = projects.ResolveRepositoryPath(project);
        Directory.Move(Path.Combine(repository, ".git"), Path.Combine(repository, ".git-disabled"));
        var second = projects.ResolveRepositoryPath(project);

        Assert.Equal(repository, first, ignoreCase: true);
        Assert.Equal(first, second, ignoreCase: true);
    }

    internal static string CreateRepository(string root, string branch)
    {
        var repository = Path.Combine(root, "repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        RunGit(repository, "init", "--initial-branch", branch);
        RunGit(repository, "config", "user.email", "tests@kittyclaw.local");
        RunGit(repository, "config", "user.name", "KittyClaw Tests");
        RunGit(repository, "commit", "--allow-empty", "-m", "initial");
        return repository;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
