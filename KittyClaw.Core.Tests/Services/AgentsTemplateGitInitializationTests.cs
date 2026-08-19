using System.Diagnostics;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class AgentsTemplateGitInitializationTests
{
    [Fact]
    public async Task Initialize_WhenGitOptionIsDisabled_DoesNotCreateRepository()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "credentials.json"), "secret fixture");
        var service = new AgentsTemplateService();

        var result = await service.InitializeAsync(workspace, overwriteConflicts: false, initializeGit: false);

        Assert.Equal(AgentsTemplateService.GitInitResult.NotAttempted, result.GitInit);
        Assert.False(Directory.Exists(Path.Combine(workspace, ".git")));
        Assert.Equal("secret fixture", await File.ReadAllTextAsync(Path.Combine(workspace, "credentials.json")));
    }

    [Fact]
    public async Task Initialize_WhenGitOptionIsEnabled_CreatesRepositoryWithoutCommit()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "existing.txt"), "preserve me");
        var service = new AgentsTemplateService();

        var result = await service.InitializeAsync(workspace, overwriteConflicts: false, initializeGit: true);

        Assert.Equal(AgentsTemplateService.GitInitResult.Created, result.GitInit);
        Assert.True(Directory.Exists(Path.Combine(workspace, ".git")));
        Assert.Contains("?? existing.txt", RunGit(workspace, "status", "--porcelain").Output);
        Assert.NotEqual(0, RunGit(workspace, "rev-parse", "--verify", "HEAD").ExitCode);
    }

    [Fact]
    public async Task Inspect_RecognizesFolderInsideParentRepositoryAndPreventsNestedInitialization()
    {
        using var temp = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "main");
        var workspace = Path.Combine(repository, "nested-project");
        Directory.CreateDirectory(workspace);
        var service = new AgentsTemplateService();

        var inspection = await AgentsTemplateService.InspectGitWorkspaceAsync(workspace);
        var result = await service.InitializeAsync(workspace, overwriteConflicts: false, initializeGit: true);

        Assert.Equal(AgentsTemplateService.GitWorkspaceKind.Repository, inspection.Kind);
        Assert.Equal(Path.GetFullPath(repository), inspection.RepositoryRoot, ignoreCase: true);
        Assert.Equal(AgentsTemplateService.GitInitResult.AlreadyExists, result.GitInit);
        Assert.False(Directory.Exists(Path.Combine(workspace, ".git")));
    }

    [Fact]
    public async Task Inspect_RecognizesMalformedGitMetadataAndDoesNotReplaceIt()
    {
        using var temp = new TempDir();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var metadata = Path.Combine(workspace, ".git");
        await File.WriteAllTextAsync(metadata, "gitdir: missing-target");
        var service = new AgentsTemplateService();

        var inspection = await AgentsTemplateService.InspectGitWorkspaceAsync(workspace);
        var result = await service.InitializeAsync(workspace, overwriteConflicts: false, initializeGit: true);

        Assert.Equal(AgentsTemplateService.GitWorkspaceKind.MetadataPresent, inspection.Kind);
        Assert.Equal(AgentsTemplateService.GitInitResult.AlreadyExists, result.GitInit);
        Assert.Equal("gitdir: missing-target", await File.ReadAllTextAsync(metadata));
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
