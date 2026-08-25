using System.Diagnostics;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class AgentMemoryAdHocTests
{
    [Fact]
    public async Task SequentialProcessor_WaitsForEachConsolidationBeforeStartingTheNext()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<int>();

        var processing = ChatMemoryConsolidationService.ProcessSequentiallyAsync(
            [1, 2],
            async (item, ct) =>
            {
                started.Add(item);
                if (item == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(ct);
                }
            },
            CancellationToken.None);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal([1], started);

        releaseFirst.TrySetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([1, 2], started);
    }

    [Fact]
    public async Task ServiceLoop_YieldsBeforeRunningTheFirstConsolidationCycle()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var loop = ChatMemoryConsolidationService.RunLoopAsync(_ =>
        {
            entered.TrySetResult();
            Thread.Sleep(500);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, TimeSpan.FromMinutes(1), cancellation.Token);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"The hosted service blocked startup for {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    [Theory]
    [InlineData("default", AdHocMemoryResult.NoChanges)]
    [InlineData("memory-update", AdHocMemoryResult.Modified)]
    public async Task Consolidation_ReportsWhetherMemoryActuallyChanged(string scenario, AdHocMemoryResult expected)
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adhoc-memory-" + scenario);
        var workspace = projects.ResolveWorkspacePath(project);
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "Programmer");
        Directory.CreateDirectory(Path.Combine(workspace, ".agents", "programmer", "memory"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "programmer", "memory", "MEMORY.md"), "# Memory\n");
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "memory-consolidation.md"),
            $"Consolidate durable lessons. <!--scenario:{scenario}-->");
        RunGit(workspace, "init");
        RunGit(workspace, "config", "user.email", "tests@kittyclaw.local");
        RunGit(workspace, "config", "user.name", "KittyClaw Tests");
        RunGit(workspace, "add", ".agents");
        RunGit(workspace, "commit", "-m", "baseline");

        var sessions = new SessionRegistry();
        var runner = new AgentRunner(sessions, new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance);
        var handler = new AgentMemoryHandler(new TicketService(projects, members), members, projects,
            runner, sessions, NullLogger.Instance);

        var result = await handler.ConsolidateAdHocConversationAsync(project.Slug, workspace,
            "programmer", "user: remember this", CancellationToken.None);

        Assert.Equal(expected, result);
        if (expected == AdHocMemoryResult.Modified)
            Assert.True(File.Exists(Path.Combine(workspace, ".agents", "programmer", "memory", "routing.md")));
    }

    [Fact]
    public async Task FailedConsolidation_DoesNotReportSuccess()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adhoc-memory-failure");
        var workspace = projects.ResolveWorkspacePath(project);
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "Programmer");
        Directory.CreateDirectory(Path.Combine(workspace, ".agents", "programmer", "memory"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "programmer", "memory", "MEMORY.md"), "# Memory\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "memory-consolidation.md"),
            "Consolidate. <!--scenario:error-exit-->");
        var sessions = new SessionRegistry();
        var handler = new AgentMemoryHandler(new TicketService(projects, members), members, projects,
            new AgentRunner(sessions, new AgentRunRegistry(), new RunConcurrencyGate(1),
                NullLogger<AgentRunner>.Instance), sessions, NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ConsolidateAdHocConversationAsync(
            project.Slug, workspace, "programmer", "user: lesson", CancellationToken.None));
    }

    [Fact]
    public async Task Service_DefersActiveChatRunThenProcessesTheSameSegment()
    {
        using var tmp = new TempDir();
        var (projects, project, workspace, members, chats, runs, service) =
            await CreateServiceAsync(tmp.Path, "adhoc-memory-active-run", "default");
        await chats.AppendAsync(project.Slug, "programmer", "user", "Remember after this run");
        var active = runs.Register(new AgentRun
        {
            RunId = "active-chat", ProjectSlug = project.Slug, TicketId = null, AgentName = "programmer",
            SkillFile = "programmer/SKILL.md", ConcurrencyGroup = $"chat:{project.Slug}:programmer",
            StartedAt = DateTime.UtcNow,
        });
        var now = DateTime.UtcNow.AddMinutes(16);

        await service.ProcessOnceAsync(now);

        var deferred = Assert.Single(await chats.ListMemoryCandidatesAsync(project.Slug, now, now));
        Assert.Equal(0, deferred.LastConsolidatedMessageId);

        runs.Complete(active.RunId, AgentRunStatus.Completed, 0);
        await service.ProcessOnceAsync(now);

        Assert.Empty(await chats.ListMemoryCandidatesAsync(project.Slug, now, now));
    }

    [Fact]
    public async Task Service_CommitFailurePreservesCheckpointThenRetryCommitsSameSegmentOnce()
    {
        using var tmp = new TempDir();
        var (projects, project, workspace, members, chats, runs, service) =
            await CreateServiceAsync(tmp.Path, "adhoc-memory-commit-retry", "memory-update");
        RunGit(workspace, "init");
        RunGit(workspace, "config", "user.email", "tests@kittyclaw.local");
        RunGit(workspace, "config", "user.name", "KittyClaw Tests");
        RunGit(workspace, "add", ".agents");
        RunGit(workspace, "commit", "-m", "baseline");
        await chats.AppendAsync(project.Slug, "programmer", "user", "Commit this durable lesson");
        var now = DateTime.UtcNow.AddMinutes(16);
        var indexLock = Path.Combine(workspace, ".git", "index.lock");
        await File.WriteAllTextAsync(indexLock, "locked");

        await service.ProcessOnceAsync(now);

        var failed = Assert.Single(await chats.ListMemoryCandidatesAsync(project.Slug, now.AddMinutes(3), now.AddMinutes(3)));
        Assert.Equal(0, failed.LastConsolidatedMessageId);
        Assert.Equal(1, failed.AttemptCount);

        File.Delete(indexLock);
        await service.ProcessOnceAsync(now.AddMinutes(3));

        Assert.Empty(await chats.ListMemoryCandidatesAsync(project.Slug, now.AddMinutes(4), now.AddMinutes(4)));
        Assert.Equal("1", RunGit(workspace, "rev-list", "--count", "--grep=chore(memory): programmer", "HEAD").Trim());
        Assert.Equal(string.Empty, RunGit(workspace, "status", "--porcelain", "--", ".agents/programmer/memory").Trim());
    }

    [Fact]
    public async Task SuccessfulWorktreeConsolidation_UsesAndFinalizesItsExistingMaintenanceRoute()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adhoc-memory-worktree-success");
        var workspace = projects.ResolveWorkspacePath(project);
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "Programmer");
        Directory.CreateDirectory(Path.Combine(workspace, ".agents", "programmer", "memory"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".agents", "programmer", "memory", "MEMORY.md"), "# Memory\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "memory-consolidation.md"),
            "Consolidate. <!--scenario:memory-update-->");
        RunGit(workspace, "init");
        RunGit(workspace, "config", "user.email", "tests@kittyclaw.local");
        RunGit(workspace, "config", "user.name", "KittyClaw Tests");
        RunGit(workspace, "add", ".agents");
        RunGit(workspace, "commit", "-m", "baseline");
        RunGit(workspace, "branch", "-M", "integration");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true,
            integrationBranch: "integration");

        var tickets = new TicketService(projects, members);
        var worktrees = new TicketWorktreeService(projects, tickets);
        var queue = new WorktreeMergeQueueService(projects, worktrees);
        var router = new DurableWriteRouter(projects, worktrees, queue);
        var chats = new ChatService(projects);
        var runs = new AgentRunRegistry();
        var sessions = new SessionRegistry();
        var handler = new AgentMemoryHandler(tickets, members, projects,
            new AgentRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance),
            sessions, NullLogger.Instance, router);
        var service = new ChatMemoryConsolidationService(projects, chats, members, runs, handler,
            NullLogger<ChatMemoryConsolidationService>.Instance, router);
        await chats.AppendAsync(project.Slug, "programmer", "user", "Remember this durable lesson");

        await service.ProcessOnceAsync(DateTime.UtcNow.AddMinutes(16))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(await chats.ListMemoryCandidatesAsync(
            project.Slug, DateTime.UtcNow.AddMinutes(17), DateTime.UtcNow.AddMinutes(17)));
        var pending = Assert.Single(await queue.ListAsync(project.Slug));
        Assert.Equal(WorktreeMergeStatus.Pending, pending.Status);
        Assert.Empty(RunGit(pending.WorktreePath, "status", "--porcelain"));

        var integrated = await queue.ProcessNextAsync(project.Slug, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        Assert.True(File.Exists(Path.Combine(
            workspace, ".agents", "programmer", "memory", "routing.md")));
    }

    [Fact]
    public async Task FailedWorktreeConsolidation_ReleasesTheMaintenanceRoute()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("adhoc-memory-route-release");
        var workspace = projects.ResolveWorkspacePath(project);
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "Programmer");
        Directory.CreateDirectory(Path.Combine(workspace, ".agents", "programmer", "memory"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "programmer", "memory", "MEMORY.md"), "# Memory\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "memory-consolidation.md"),
            "Consolidate. <!--scenario:error-exit-->");
        RunGit(workspace, "init");
        RunGit(workspace, "config", "user.email", "tests@kittyclaw.local");
        RunGit(workspace, "config", "user.name", "KittyClaw Tests");
        RunGit(workspace, "add", ".agents");
        RunGit(workspace, "commit", "-m", "baseline");
        RunGit(workspace, "branch", "-M", "integration");
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true,
            integrationBranch: "integration");

        var tickets = new TicketService(projects, members);
        var worktrees = new TicketWorktreeService(projects, tickets);
        var queue = new WorktreeMergeQueueService(projects, worktrees);
        var router = new DurableWriteRouter(projects, worktrees, queue);
        var chats = new ChatService(projects);
        var runs = new AgentRunRegistry();
        var sessions = new SessionRegistry();
        var handler = new AgentMemoryHandler(tickets, members, projects,
            new AgentRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance),
            sessions, NullLogger.Instance);
        var service = new ChatMemoryConsolidationService(projects, chats, members, runs, handler,
            NullLogger<ChatMemoryConsolidationService>.Instance, router);
        await chats.AppendAsync(project.Slug, "programmer", "user", "This attempt must fail safely");

        await service.ProcessOnceAsync(DateTime.UtcNow.AddMinutes(16));

        var preserved = Assert.Single(await queue.ListAsync(project.Slug));
        var remaining = RunGit(preserved.WorktreePath, "status", "--porcelain", "--untracked-files=all").Trim();
        Assert.True(string.IsNullOrEmpty(remaining), $"Failed consolidation left durable worktree dirty: {remaining}");
        var probe = await router.ResolveAsync(project.Slug, null, [".dashboard"])
            .WaitAsync(TimeSpan.FromSeconds(2));
        await router.CloseOrPreserveExecutionAsync(project.Slug, probe, "release test probe");
        Assert.Equal(WorktreeMergeStatus.Completed,
            Assert.Single(await queue.ListAsync(project.Slug)).Status);
    }

    private static async Task<(ProjectService Projects, KittyClaw.Core.Models.Project Project,
        string Workspace, MemberService Members, ChatService Chats, AgentRunRegistry Runs,
        ChatMemoryConsolidationService Service)> CreateServiceAsync(string root, string projectName, string scenario)
    {
        var projects = new ProjectService(root);
        var project = await projects.CreateProjectAsync(projectName);
        var workspace = projects.ResolveWorkspacePath(project);
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "Programmer");
        Directory.CreateDirectory(Path.Combine(workspace, ".agents", "programmer", "memory"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "programmer", "memory", "MEMORY.md"), "# Memory\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "memory-consolidation.md"),
            $"Consolidate durable lessons. <!--scenario:{scenario}-->");
        var chats = new ChatService(projects);
        var runs = new AgentRunRegistry();
        var sessions = new SessionRegistry();
        var handler = new AgentMemoryHandler(new TicketService(projects, members), members, projects,
            new AgentRunner(sessions, runs, new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance),
            sessions, NullLogger.Instance);
        var service = new ChatMemoryConsolidationService(projects, chats, members, runs, handler,
            NullLogger<ChatMemoryConsolidationService>.Instance);
        return (projects, project, workspace, members, chats, runs, service);
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
