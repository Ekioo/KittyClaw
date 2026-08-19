using System.Diagnostics;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.QaRunner;

namespace KittyClaw.Core.Tests.QaRunner;

public sealed class ScenarioGitFixtureTests
{
    [Fact]
    public void CreatesRepositoryAndCommitsToRequestedWorktree()
    {
        using var root = new TempDir();
        var repository = Path.Combine(root.Path, "repository");
        ScenarioRunner.CreateGitRepository(repository, "integration");
        var worktree = Path.Combine(root.Path, "repository.worktrees", "ticket-42");
        Git(repository, "worktree", "add", "-b", "ticket/42", worktree, "integration");

        ScenarioRunner.CommitGitFile(repository, "ticket/42", "nested/feature.txt", "ready\n");

        Assert.Equal("ready\n", File.ReadAllText(Path.Combine(worktree, "nested", "feature.txt")).Replace("\r\n", "\n"));
        Assert.Equal("fixture: nested/feature.txt", Git(worktree, "log", "-1", "--pretty=%s").Trim());
        Assert.Empty(Git(worktree, "status", "--porcelain"));
    }

    [Fact]
    public void RejectsFixtureFileOutsideWorktree()
    {
        using var root = new TempDir();
        var repository = Path.Combine(root.Path, "repository");
        ScenarioRunner.CreateGitRepository(repository, "integration");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ScenarioRunner.CommitGitFile(repository, "integration", "../outside.txt", "no"));

        Assert.Contains("escapes worktree", error.Message);
        Assert.False(File.Exists(Path.Combine(root.Path, "outside.txt")));
    }

    private static string Git(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
