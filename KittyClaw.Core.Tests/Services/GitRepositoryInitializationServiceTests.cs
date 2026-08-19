using System.Diagnostics;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class GitRepositoryInitializationServiceTests
{
    [Fact]
    public async Task Initialize_CreatesRepositoryWithoutStagingOrCommittingWorkspaceFiles()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "credentials.json"), "secret fixture");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("git-init");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        await projects.UpdateProjectAsync(project.Slug, workspace);
        var service = new GitRepositoryInitializationService(projects);

        var result = await service.InitializeAsync(project.Slug);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(workspace), result.RepositoryRoot, ignoreCase: true);
        Assert.True(Directory.Exists(Path.Combine(workspace, ".git")));
        Assert.Contains("?? credentials.json", RunGit(workspace, "status", "--porcelain").Output);
        Assert.NotEqual(0, RunGit(workspace, "rev-parse", "--verify", "HEAD").ExitCode);
        var status = await service.GetStatusAsync(project.Slug);
        Assert.True(status!.WorkspaceConfigured);
        Assert.True(status.IsRepository);
    }

    [Fact]
    public async Task Initialize_RejectsMissingWorkspace()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("missing-workspace");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        await projects.UpdateProjectAsync(project.Slug, Path.Combine(temp.Path, "absent"));
        var service = new GitRepositoryInitializationService(projects);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync(project.Slug));

        Assert.Contains("n’existe pas", error.Message);
    }

    [Fact]
    public async Task Initialize_RejectsExistingRepositoryWithoutChangingHead()
    {
        using var temp = new TempDir();
        var workspace = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "main");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("existing-repository");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        await projects.UpdateProjectAsync(project.Slug, workspace);
        var service = new GitRepositoryInitializationService(projects);
        var headBefore = RunGit(workspace, "rev-parse", "HEAD").Output.Trim();

        var error = await Assert.ThrowsAsync<GitRepositoryAlreadyExistsException>(() => service.InitializeAsync(project.Slug));

        Assert.Contains("déjà", error.Message);
        Assert.Equal(headBefore, RunGit(workspace, "rev-parse", "HEAD").Output.Trim());
    }

    [Fact]
    public async Task Status_DoesNotOfferInitializationUntilWorkspaceIsExplicitlyConfigured()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("no-workspace-config");
        var service = new GitRepositoryInitializationService(projects);

        var status = await service.GetStatusAsync(project.Slug);

        Assert.NotNull(status);
        Assert.False(status.WorkspaceConfigured);
        Assert.False(status.IsRepository);
    }

    [Fact]
    public async Task Initialize_RejectsExistingGitFileEvenWhenItIsMalformed()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace-with-git-file");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, ".git"), "gitdir: missing-target");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("git-file");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
        await projects.UpdateProjectAsync(project.Slug, workspace);
        var service = new GitRepositoryInitializationService(projects);

        var status = await service.GetStatusAsync(project.Slug);
        var error = await Assert.ThrowsAsync<GitRepositoryAlreadyExistsException>(() => service.InitializeAsync(project.Slug));

        Assert.True(status!.GitMetadataPresent);
        Assert.False(status.IsRepository);
        Assert.Contains("métadonnées Git", error.Message);
        Assert.Equal("gitdir: missing-target", await File.ReadAllTextAsync(Path.Combine(workspace, ".git")));
    }

    private static (int ExitCode, string Output) RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
