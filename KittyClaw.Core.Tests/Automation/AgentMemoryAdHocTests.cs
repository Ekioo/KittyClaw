using System.Diagnostics;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class AgentMemoryAdHocTests
{
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
