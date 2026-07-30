using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Workspace path validation (backport analysis §2.6): the workspace is where KittyClaw
/// writes .agents/** and launches permission-less claude subprocesses, and the API that
/// sets it is open to agents. Relative paths, "..", filesystem roots and system
/// directories are rejected at write time with an actionable message; existing projects
/// are never re-validated (no project bricked by an upgrade).
/// </summary>
public class WorkspacePathValidationTests
{
    [Theory]
    [InlineData("relative/path")]
    [InlineData("./here")]
    [InlineData("work")]
    public void RelativePaths_AreRejected(string path)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectService.ValidateWorkspacePath(path));
        Assert.Contains("absolu", ex.Message);
    }

    [Fact]
    public void ParentTraversal_IsRejected()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\projects\..\Windows" : "/home/user/../../etc";
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectService.ValidateWorkspacePath(path));
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void DriveRoot_IsRejected()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\" : "/";
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectService.ValidateWorkspacePath(path));
        Assert.Contains("racine", ex.Message);
    }

    [Fact]
    public void SystemDirectories_AreRejected_EqualOrInside()
    {
        var (systemDir, inside) = OperatingSystem.IsWindows()
            ? (Environment.GetFolderPath(Environment.SpecialFolder.Windows),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"))
            : ("/etc", "/etc/kittyclaw");

        Assert.Throws<InvalidOperationException>(() => ProjectService.ValidateWorkspacePath(systemDir));
        Assert.Throws<InvalidOperationException>(() => ProjectService.ValidateWorkspacePath(inside));
    }

    [Fact]
    public void SiblingOfSystemDir_IsAccepted()
    {
        // Prefix matching must be per-segment: "C:\Windows-Projects" is NOT inside "C:\Windows".
        var path = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "-Projects"
            : "/etc-projects";
        ProjectService.ValidateWorkspacePath(path); // must not throw
    }

    [Fact]
    public void NormalAbsolutePath_IsAccepted()
    {
        ProjectService.ValidateWorkspacePath(Path.Combine(Path.GetTempPath(), "kittyclaw-ws")); // must not throw
    }

    [Fact]
    public async Task UpdateProject_RejectsInvalidWorkspace_AndKeepsTheOldOne()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("workspace-validation-test");
        var good = Path.Combine(tmp.Path, "good-ws");
        await projects.UpdateProjectAsync(project.Slug, good);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projects.UpdateProjectAsync(project.Slug, "relative/ws"));

        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.Equal(good, loaded!.WorkspacePath);
    }

    [Fact]
    public async Task UpdateProject_NullWorkspace_StillClearsToDefault()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("workspace-clear-test");
        await projects.UpdateProjectAsync(project.Slug, Path.Combine(tmp.Path, "ws"));

        await projects.UpdateProjectAsync(project.Slug, null);
        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.Null(loaded!.WorkspacePath);
    }
}
