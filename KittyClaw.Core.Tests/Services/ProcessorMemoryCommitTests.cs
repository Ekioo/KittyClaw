using System.Diagnostics;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProcessorMemoryCommitTests
{
    [Fact]
    public async Task Capitalization_and_recovery_commit_only_memory_preserving_other_staged_files()
    {
        using var temp = new TempDir();
        var repo = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "integration");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("Processor memory commits");
        await projects.UpdateProjectAsync(project.Slug, repo, worktreesEnabled: false);
        var memory = new ColumnMemoryCapitalizationService(projects);
        await File.WriteAllTextAsync(Path.Combine(repo, "owner.txt"), "Unrelated staged owner work\n");
        await Git(repo, "add", "owner.txt");

        var result = await memory.CapitalizeAsync(project.Slug, 12, "first", ["Verify all acceptance criteria before completing a ticket."]);

        Assert.Equal(MemoryCapitalizationStatus.Succeeded, result.Status);
        Assert.Empty(await Git(repo, "status", "--porcelain", "--", ".agents/processors"));
        Assert.Equal("owner.txt", (await Git(repo, "diff", "--cached", "--name-only")).Trim());
        var committed = await Git(repo, "show", "--format=", "--name-only", "HEAD");
        Assert.Contains("MEMORY.md", committed);
        Assert.DoesNotContain("pipeline-lessons.md", committed);
        Assert.DoesNotContain("owner.txt", committed);
        var checkpointHead = await Git(repo, "rev-parse", "HEAD");
        var replay = await memory.CapitalizeAsync(project.Slug, 12, "first", ["Verify all acceptance criteria before completing a ticket."]);
        Assert.Equal(MemoryCapitalizationStatus.Succeeded, replay.Status);
        Assert.Equal(checkpointHead, await Git(repo, "rev-parse", "HEAD"));

        var index = Path.Combine(repo, ".agents", "processors", "column-12", "memory", "MEMORY.md");
        await File.AppendAllTextAsync(index, "\nAn agent added another useful lesson.\n");
        await memory.CapitalizeAsync(project.Slug, 12, "without-new-lessons", []);
        Assert.Empty(await Git(repo, "status", "--porcelain", "--", ".agents/processors"));
        Assert.Contains("another useful lesson", await Git(repo, "show", "HEAD:.agents/processors/column-12/memory/MEMORY.md"));
        var head = await Git(repo, "rev-parse", "HEAD");
        await memory.CapitalizeAsync(project.Slug, 12, "repeated-empty", []);
        Assert.Equal(head, await Git(repo, "rev-parse", "HEAD"));
        Assert.Equal("owner.txt", (await Git(repo, "diff", "--cached", "--name-only")).Trim());
    }

    [Fact]
    public async Task Ticket_memory_is_committed_immediately_without_dirtying_primary_checkout()
    {
        using var temp = new TempDir();
        var repo = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "integration");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("Ticket memory commits");
        await projects.UpdateProjectAsync(project.Slug, repo, worktreesEnabled: true, integrationBranch: "integration");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Memory checkpoint");
        var worktrees = new TicketWorktreeService(projects, tickets);
        var router = new DurableWriteRouter(projects, worktrees);
        var memory = new ColumnMemoryCapitalizationService(projects, router);

        var result = await memory.CapitalizeAsync(project.Slug, 8, "ticket-run",
            ["Persist reusable lessons before handing the ticket to the next processor."], ticketId: ticket.Id);

        Assert.Equal(MemoryCapitalizationStatus.Succeeded, result.Status);
        var route = await router.ResolveAsync(project.Slug, ticket.Id, [".agents/processors/column-8/memory"]);
        Assert.Empty(await Git(route.RootPath, "status", "--porcelain"));
        Assert.Contains("MEMORY.md", await Git(route.RootPath, "show", "--format=", "--name-only", "HEAD"));
        Assert.Empty(await Git(repo, "status", "--porcelain"));
        Assert.False(Directory.Exists(Path.Combine(repo, ".agents", "processors")));
    }

    [Fact]
    public async Task Ignored_untracked_memory_stays_ignored_and_no_Git_hook_is_installed()
    {
        using var temp = new TempDir();
        var repo = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "main");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), ".agents/\n");
        await Git(repo, "add", ".gitignore");
        await Git(repo, "commit", "-m", "Ignore local agent memory");
        var before = await Git(repo, "rev-parse", "HEAD");
        var hooks = Directory.GetFiles(Path.Combine(repo, ".git", "hooks")).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText);
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("Ignored memory");
        await projects.UpdateProjectAsync(project.Slug, repo, worktreesEnabled: false);
        var service = new ColumnMemoryCapitalizationService(projects);
        Assert.Equal(MemoryCapitalizationStatus.Succeeded, (await service.CapitalizeAsync(project.Slug, 1, "ignored",
            ["Keep local-only memory ignored instead of forcing files into Git."])).Status);
        Assert.Empty(await Git(repo, "ls-files", "--", ".agents"));
        Assert.Equal(before, await Git(repo, "rev-parse", "HEAD"));
        var after = Directory.GetFiles(Path.Combine(repo, ".git", "hooks")).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText);
        Assert.Equal(hooks.OrderBy(x => x.Key), after.OrderBy(x => x.Key));
    }

    [Fact]
    public async Task Creating_processor_commits_memory_immediately_but_reading_it_does_not_retry_pending_commits()
    {
        using var temp = new TempDir();
        var repo = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "main");
        var projects = new ProjectService(Path.Combine(temp.Path, "data"));
        var project = await projects.CreateProjectAsync("Initial memory checkpoint");
        await projects.UpdateProjectAsync(project.Slug, repo, worktreesEnabled: false);
        var column = (await new ColumnService(projects).ListColumnsAsync(project.Slug))[0];
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        await processors.SaveAsync(project.Slug, column.Id, "Worker", "Work.", null, true, 10, [], [], []);
        var path = (await processors.GetMemoryIndexPathAsync(project.Slug, column.Id))!;
        Assert.Contains("Persistent lessons", await Git(repo, "show", $"HEAD:.agents/processors/column-{column.Id}/memory/MEMORY.md"));
        var before = await Git(repo, "rev-parse", "HEAD");
        await File.AppendAllTextAsync(path, "\nPending lesson from an interrupted external write.\n");
        await File.WriteAllTextAsync(Path.Combine(repo, ".git", "hooks", "pre-commit"), "#!/bin/sh\nexit 1\n");
        await processors.ListEnabledAsync(project.Slug);
        Assert.Equal(path, await processors.GetMemoryIndexPathAsync(project.Slug, column.Id));
        Assert.Equal(before, await Git(repo, "rev-parse", "HEAD"));
        Assert.Contains("Pending lesson", await File.ReadAllTextAsync(path));
    }

    private static async Task<string> Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
