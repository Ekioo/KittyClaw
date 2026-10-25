using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public class WorkingFilesPathTests
{
    [Fact]
    public async Task FolderSetting_PersistsPreservesWorkspaceAndClears()
    {
        using var tmp = new TempDir();
        var service = new ProjectService(tmp.Path);
        var project = await service.CreateProjectAsync("Working folder");
        var workspace = Path.Combine(tmp.Path, "workspace");
        await service.UpdateProjectAsync(project.Slug, workspace);
        var folder = Path.Combine(tmp.Path, "artifacts");
        await service.UpdateProjectAsync(project.Slug, null, workingFilesPath: "  " + folder + "  ");
        var reloaded = await new ProjectService(tmp.Path).GetProjectAsync(project.Slug);
        Assert.Equal(folder, reloaded!.WorkingFilesPath);
        Assert.Equal(workspace, reloaded.WorkspacePath);
        await service.UpdateProjectAsync(project.Slug, workspace);
        Assert.Equal(folder, (await service.GetProjectAsync(project.Slug))!.WorkingFilesPath);
        await service.UpdateProjectAsync(project.Slug, null, workingFilesPath: "");
        reloaded = await service.GetProjectAsync(project.Slug);
        Assert.Null(reloaded!.WorkingFilesPath);
        Assert.Equal(workspace, reloaded.WorkspacePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunFolder_IsCreatedIsolatedAndReused(bool custom)
    {
        using var tmp = new TempDir();
        var service = new ProjectService(tmp.Path);
        var project = await service.CreateProjectAsync("Run artifacts");
        if (custom) project.WorkingFilesPath = Path.Combine(tmp.Path, "custom");
        var root = custom ? project.WorkingFilesPath! : Path.Combine(tmp.Path, "artifacts", project.Slug);
        var first = service.PrepareRunWorkingFilesDirectory(project, "run-one");
        var second = service.PrepareRunWorkingFilesDirectory(project, "run-two");
        Assert.Equal(Path.Combine(root, "run-one"), first);
        Assert.Equal(Path.Combine(root, "run-two"), second);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        await File.WriteAllTextAsync(Path.Combine(first, "proof.txt"), "retained evidence");
        Assert.Equal(first, service.PrepareRunWorkingFilesDirectory(project, "run-one"));
        Assert.Equal("retained evidence", await File.ReadAllTextAsync(Path.Combine(first, "proof.txt")));
        Assert.False(File.Exists(Path.Combine(second, "proof.txt")));
        Assert.False(first.StartsWith(service.ResolveWorkspacePath(project) + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task RunFolder_RejectsTraversalAndReportsCreationFailure()
    {
        using var tmp = new TempDir();
        var service = new ProjectService(tmp.Path);
        var project = await service.CreateProjectAsync("Invalid artifacts");
        Assert.Throws<ArgumentException>(() => service.PrepareRunWorkingFilesDirectory(project, "../escape"));
        var file = Path.Combine(tmp.Path, "blocked");
        await File.WriteAllTextAsync(file, "not a directory");
        project.WorkingFilesPath = file;
        var failure = Assert.Throws<InvalidOperationException>(() => service.PrepareRunWorkingFilesDirectory(project, "run-one"));
        Assert.Contains(Path.Combine(file, "run-one"), failure.Message);
    }

    [Fact]
    public async Task Runner_StopsBeforeProviderLaunchWhenRunFolderCannotBeCreated()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("Blocked run artifacts");
        var folder = Path.Combine(tmp.Path, "blocked");
        await projects.UpdateProjectAsync(project.Slug, null, workingFilesPath: folder);
        // Simulate a path becoming a file after the setting was validated.
        await File.WriteAllTextAsync(folder, "not a directory");
        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunner>.Instance, projects: projects);
        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = project.Slug, WorkspacePath = projects.ResolveWorkspacePath(project),
            AgentName = "agent", SkillFile = "(inline)", InlineSkillContent = "Test instructions"
        }, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("Cannot create the run working files folder"));
    }

    [Fact]
    public async Task FolderSetting_RejectsRelativePathsAndFiles()
    {
        using var tmp = new TempDir();
        var service = new ProjectService(tmp.Path);
        var project = await service.CreateProjectAsync("Validation");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateProjectAsync(project.Slug, null, workingFilesPath: "relative"));
        var file = Path.Combine(tmp.Path, "existing.txt");
        await File.WriteAllTextAsync(file, "content");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateProjectAsync(project.Slug, null, workingFilesPath: file));
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("column-processor", false)]
    [InlineData("column-processor", true)]
    [InlineData("automation", true)]
    public async Task Prompt_UsesConfiguredFolderAcrossSessionTypes(string scope, bool resume)
    {
        using var tmp = new TempDir();
        var folder = Path.Combine(tmp.Path, "proofs");
        var ctx = new AgentRunContext
        {
            ProjectSlug = "test", WorkspacePath = tmp.Path,
            WorkingFilesPath = folder, AgentName = "agent", SkillFile = "(inline)",
            SessionScope = scope, TicketId = scope == "chat" ? null : 42,
            ExtraContext = "Continue the task"
        };
        var prompt = await AgentRunner.BuildPromptAsync(ctx, "Skill", resume, CancellationToken.None);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(folder), prompt);
        Assert.Contains("screenshots, evidence", prompt);
        Assert.Contains("may be cleaned up or deleted at any time", prompt);
        Assert.Contains("Never leave unique or valuable data", prompt);
        Assert.Contains("save all durable outputs and required evidence", prompt);
        Assert.Contains("even when executing in a worktree", prompt);
    }
}
