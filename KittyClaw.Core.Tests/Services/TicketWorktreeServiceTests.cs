using System.Diagnostics;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

[Collection("MockClaude")]
public sealed class TicketWorktreeServiceTests
{
    [Fact]
    public async Task RepeatedResolution_ReusesCanonicalPathAndBranch()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Root");

        var first = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, ticket.Id, CancellationToken.None);
        var second = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, ticket.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal($"ticket/{ticket.Id}", first.Branch);
        Assert.Equal(Path.GetFullPath(first.Path),
            Path.GetFullPath(RunGit(first.Path, "rev-parse", "--show-toplevel").Trim()), ignoreCase: true);
        Assert.Equal(first.Branch, RunGit(first.Path, "branch", "--show-current").Trim());
        Assert.Equal(1, RunGit(fixture.Repository, "worktree", "list", "--porcelain")
            .Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Count(line => Path.GetFullPath(line.TrimEnd('\r')[9..]).Equals(
                Path.GetFullPath(first.Path), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task NestedConfiguredRepository_CreatesWorktreeBesideNestedRepositoryOnly()
    {
        using var fixture = await Fixture.CreateAsync(nested: true);
        var ticket = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Nested root");
        var outerWorktreesBefore = RunGit(fixture.Workspace, "worktree", "list", "--porcelain");

        var worktree = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, ticket.Id, CancellationToken.None);

        Assert.NotNull(worktree);
        Assert.Equal(Path.Combine(fixture.Repository + ".worktrees", $"ticket-{ticket.Id}"), worktree.Path, ignoreCase: true);
        Assert.Contains($"branch refs/heads/ticket/{ticket.Id}", RunGit(fixture.Repository, "worktree", "list", "--porcelain"));
        Assert.Equal(outerWorktreesBefore, RunGit(fixture.Workspace, "worktree", "list", "--porcelain"));
        Assert.NotEqual(0, RunGitExitCode(fixture.Workspace, "show-ref", "--verify", "--quiet", $"refs/heads/ticket/{ticket.Id}"));
    }

    [Fact]
    public async Task ParentAndChild_ShareWorktree_WhileRootsRemainDistinct()
    {
        using var fixture = await Fixture.CreateAsync();
        var parent = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Parent");
        var child = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Child", parentId: parent.Id);
        var other = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Other root");

        var parentWorktree = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, parent.Id, CancellationToken.None);
        var childWorktree = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, child.Id, CancellationToken.None);
        var otherWorktree = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, other.Id, CancellationToken.None);

        Assert.Equal(parentWorktree, childWorktree);
        Assert.NotEqual(parentWorktree!.Path, otherWorktree!.Path);
        Assert.NotEqual(parentWorktree.Branch, otherWorktree.Branch);
    }

    [Fact]
    public async Task Inspect_ReportsSharedIdentityAndDirtyState_WithoutCreatingMissingWorktree()
    {
        using var fixture = await Fixture.CreateAsync();
        var parent = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Parent");
        var child = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Child", parentId: parent.Id);

        var before = await fixture.Worktrees.InspectAsync(fixture.ProjectSlug, child.Id);
        Assert.NotNull(before);
        Assert.False(before.Exists);
        Assert.Equal(parent.Id, before.RootTicketId);
        Assert.Equal($"ticket/{parent.Id}", before.Branch);

        await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, parent.Id, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(before.Path, "uncommitted.txt"), "keep");
        var parentState = await fixture.Worktrees.InspectAsync(fixture.ProjectSlug, parent.Id);
        var childState = await fixture.Worktrees.InspectAsync(fixture.ProjectSlug, child.Id);

        Assert.Equal(parentState, childState);
        Assert.True(childState!.Exists);
        Assert.True(childState.IsDirty);
    }

    [Fact]
    public async Task DisabledMode_PreservesWorkspaceAndCreatesNothing()
    {
        using var fixture = await Fixture.CreateAsync(enable: false);
        var ticket = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Root");

        var result = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, ticket.Id, CancellationToken.None);

        Assert.Null(result);
        Assert.False(Directory.Exists(fixture.Repository + ".worktrees"));
    }

    [Fact]
    public async Task GitFailure_IsPublishedOnRun_WithoutChangingPrimaryCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Root");
        var expectedPath = Path.Combine(fixture.Root.Path, Path.GetFileName(fixture.Repository) + ".worktrees", $"ticket-{ticket.Id}");
        Directory.CreateDirectory(expectedPath);
        await File.WriteAllTextAsync(Path.Combine(expectedPath, "collision.txt"), "keep");
        var headBefore = RunGit(fixture.Repository, "rev-parse", "HEAD");
        var statusBefore = RunGit(fixture.Repository, "status", "--porcelain");
        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance, worktrees: fixture.Worktrees);

        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = fixture.ProjectSlug,
            WorkspacePath = fixture.Repository,
            AgentName = "test",
            SkillFile = "missing.md",
            TicketId = ticket.Id,
        }, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("not registered with Git"));
        Assert.Equal(headBefore, RunGit(fixture.Repository, "rev-parse", "HEAD"));
        Assert.Equal(statusBefore, RunGit(fixture.Repository, "status", "--porcelain"));
        Assert.True(File.Exists(Path.Combine(expectedPath, "collision.txt")));
    }

    [Fact]
    public async Task TicketRun_WritesOnlyInsideCanonicalWorktree_AndPublishesWorkingDirectory()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "KittyClaw-Front 254 regression");
        var scenarios = Path.Combine(fixture.Root.Path, "scenarios");
        Directory.CreateDirectory(scenarios);
        await File.WriteAllTextAsync(Path.Combine(scenarios, "worktree-write.ndjson"), string.Join('\n',
        [
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            "{\"_meta\":{\"write_file\":{\"path\":\"delivery/final.txt\",\"content\":\"audited\"}}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":1,\"num_turns\":1}",
        ]));
        TestSkillBuilder.Create(fixture.Repository, "worktree-writer", scenario: "worktree-write");
        var primaryStateBefore = RunGit(fixture.Repository, "status", "--porcelain=v1", "--untracked-files=all",
            "--", ".", ":(exclude).agents/channel/**");
        var runData = Path.Combine(fixture.Root.Path, "run-data");
        var runRegistry = new AgentRunRegistry(new RunLogStore(runData));
        var runner = new AgentRunner(new SessionRegistry(), runRegistry, new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance, worktrees: fixture.Worktrees);

        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = fixture.ProjectSlug,
            WorkspacePath = fixture.Repository,
            AgentName = "worktree-writer",
            SkillFile = "worktree-writer/SKILL.md",
            TicketId = ticket.Id,
            MaxTurns = 1,
            Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarios },
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var worktree = await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, ticket.Id, CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(Path.GetFullPath(worktree!.Path), Path.GetFullPath(run.WorkingDirectory!), ignoreCase: true);
        Assert.Equal("audited", await File.ReadAllTextAsync(Path.Combine(worktree.Path, "delivery", "final.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "delivery", "final.txt")));
        Assert.Equal(primaryStateBefore,
            RunGit(fixture.Repository, "status", "--porcelain=v1", "--untracked-files=all",
                "--", ".", ":(exclude).agents/channel/**"));
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "worktree" && e.Text.Contains(worktree.Path));
        Assert.Equal(run.WorkingDirectory,
            new AgentRunRegistry(new RunLogStore(runData)).Get(run.RunId)!.WorkingDirectory);
    }

    [Fact]
    public async Task TicketRun_DetectsMemoryWriteInPrimaryRepository()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Memory consolidation overlap");
        var scenarios = Path.Combine(fixture.Root.Path, "scenarios");
        Directory.CreateDirectory(scenarios);
        await File.WriteAllTextAsync(Path.Combine(scenarios, "worktree-memory-delay.ndjson"), string.Join('\n',
        [
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            "{\"_meta\":{\"delay_ms\":1000}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":1000,\"num_turns\":1}",
        ]));
        TestSkillBuilder.Create(fixture.Repository, "worktree-agent", scenario: "worktree-memory-delay");
        var memoryDirectory = Path.Combine(fixture.Repository, ".agents", "processors", "column-25", "memory");
        Directory.CreateDirectory(memoryDirectory);
        var memoryFile = Path.Combine(memoryDirectory, "MEMORY.md");
        await File.WriteAllTextAsync(memoryFile, "before");
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance, worktrees: fixture.Worktrees);

        var running = runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = fixture.ProjectSlug,
            WorkspacePath = fixture.Repository,
            AgentName = "worktree-agent",
            SkillFile = "worktree-agent/SKILL.md",
            TicketId = ticket.Id,
            MaxTurns = 1,
            Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarios },
            OnEventHook = e =>
            {
                if (e.Kind == "launch") launched.TrySetResult();
            },
        }, CancellationToken.None);
        await launched.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(memoryFile, "after");
        var run = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Text.Contains("Worktree boundary violation"));
    }

    [Fact]
    public void InternalRetries_PreserveResolvedWorktree()
    {
        var context = new AgentRunContext
        {
            ProjectSlug = "project",
            WorkspacePath = "primary",
            ExecutionWorkspacePath = "ticket-worktree",
            AgentName = "agent",
            SkillFile = "agent/SKILL.md",
            FallbackTarget = AgentDispatchTarget.ClaudeDefault with { Model = "fallback" },
        };

        Assert.Equal("ticket-worktree", context.WithFallback().ExecutionWorkspacePath);
        Assert.Equal("ticket-worktree", context.WithChatReplay("continue").ExecutionWorkspacePath);
    }

    [Fact]
    public async Task RunsForSameRoot_AreSerialized_WhileDifferentRootsRunConcurrently()
    {
        using var fixture = await Fixture.CreateAsync();
        var root = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Root");
        var child = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Child", parentId: root.Id);
        var other = await fixture.Tickets.CreateTicketAsync(fixture.ProjectSlug, "Other root");
        var scenarios = Path.Combine(fixture.Root.Path, "scenarios");
        Directory.CreateDirectory(scenarios);
        await File.WriteAllTextAsync(Path.Combine(scenarios, "worktree-delay.ndjson"), string.Join('\n',
        [
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            "{\"_meta\":{\"delay_ms\":3000}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":3000,\"num_turns\":1}",
        ]));
        TestSkillBuilder.Create(fixture.Repository, "worktree-agent", scenario: "worktree-delay");
        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(4),
            NullLogger<AgentRunner>.Instance, worktrees: fixture.Worktrees);

        // Worktree provisioning has its own cross-root concurrency and timing concerns.
        // Prepare both canonical worktrees so this test measures only the execution
        // invariant: one root is serialized while a distinct root can run concurrently.
        await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, root.Id, CancellationToken.None);
        await fixture.Worktrees.ResolveAsync(fixture.ProjectSlug, other.Id, CancellationToken.None);

        AgentRunContext Context(int ticketId, Action<StreamEvent>? onEvent = null) => new()
        {
            ProjectSlug = fixture.ProjectSlug,
            WorkspacePath = fixture.Repository,
            AgentName = $"agent-{ticketId}",
            SkillFile = "worktree-agent/SKILL.md",
            TicketId = ticketId,
            MaxTurns = 1,
            Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarios },
            OnEventHook = onEvent,
        };

        var rootLaunched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootRun = runner.RunAsync(Context(root.Id, e =>
        {
            if (e.Kind == "launch") rootLaunched.TrySetResult();
        }), CancellationToken.None);
        await rootLaunched.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var runs = await Task.WhenAll(rootRun,
            runner.RunAsync(Context(child.Id), CancellationToken.None),
            runner.RunAsync(Context(other.Id), CancellationToken.None));

        Assert.All(runs, run => Assert.True(run.Status == AgentRunStatus.Completed,
            string.Join(" | ", run.SnapshotBuffer().Select(e => $"{e.Kind}: {e.Text}"))));
        var intervals = runs.Select(run =>
        {
            var events = run.SnapshotBuffer();
            return (
                Launch: events.Single(e => e.Kind == "launch").At,
                Result: events.Single(e => e.Kind == "result").At);
        }).ToArray();
        var firstSameRoot = intervals[0].Launch <= intervals[1].Launch ? intervals[0] : intervals[1];
        var secondSameRoot = intervals[0].Launch <= intervals[1].Launch ? intervals[1] : intervals[0];
        Assert.True(firstSameRoot.Result <= secondSameRoot.Launch,
            "The second same-root run launched before the first one completed.");
        Assert.True(intervals[2].Launch < firstSameRoot.Result ||
                    (intervals[2].Launch < secondSameRoot.Result && intervals[2].Result > secondSameRoot.Launch),
            "The distinct-root run did not overlap either same-root execution.");
        Assert.Contains(runs.SelectMany(run => run.SnapshotBuffer()),
            e => e.Kind == "queued" && e.Text.Contains("Waiting for worktree"));
    }

    private sealed class Fixture : IDisposable
    {
        public TempDir Root { get; }
        public string Repository { get; }
        public string Workspace { get; }
        public string ProjectSlug { get; }
        public TicketService Tickets { get; }
        public TicketWorktreeService Worktrees { get; }

        private Fixture(TempDir root, string workspace, string repository, string projectSlug, TicketService tickets,
            TicketWorktreeService worktrees)
        {
            Root = root;
            Workspace = workspace;
            Repository = repository;
            ProjectSlug = projectSlug;
            Tickets = tickets;
            Worktrees = worktrees;
        }

        public static async Task<Fixture> CreateAsync(bool enable = true, bool nested = false)
        {
            var root = new TempDir();
            var workspace = ProjectWorktreeSettingsTests.CreateRepository(root.Path, nested ? "outer" : "integration");
            var repository = nested
                ? ProjectWorktreeSettingsTests.CreateRepository(workspace, "integration")
                : workspace;
            var projects = new ProjectService(Path.Combine(root.Path, "data"));
            var project = await projects.CreateProjectAsync("worktree-resolution");
            await projects.UpdateProjectAsync(project.Slug, workspace);
            if (enable)
                await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true, integrationBranch: "integration",
                    repositoryPath: nested ? Path.GetRelativePath(workspace, repository) : null);
            else
                await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: false);
            var tickets = new TicketService(projects, new MemberService(projects));
            return new Fixture(root, workspace, repository, project.Slug, tickets, new TicketWorktreeService(projects, tickets));
        }

        public void Dispose() => Root.Dispose();
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }

    private static int RunGitExitCode(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
