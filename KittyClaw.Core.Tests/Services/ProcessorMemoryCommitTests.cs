using System.Diagnostics;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProcessorMemoryCommitTests
{
    [Fact]
    public async Task Capitalization_and_recovery_commit_only_memory_even_if_ignored_or_other_files_are_staged()
    {
        using var temp = new TempDir();
        var repo = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "integration");
        await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), ".agents/\n");
        await Git(repo, "add", ".gitignore");
        await Git(repo, "commit", "-m", "Ignore local agent files");
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
        Assert.Contains("pipeline-lessons.md", committed);
        Assert.DoesNotContain("owner.txt", committed);

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
        Assert.Contains("pipeline-lessons.md", await Git(route.RootPath, "show", "--format=", "--name-only", "HEAD"));
        Assert.Empty(await Git(repo, "status", "--porcelain"));
        Assert.False(Directory.Exists(Path.Combine(repo, ".agents", "processors")));
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
