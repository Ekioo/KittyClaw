using System.Diagnostics;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class DurableWriteRouterTests
{
    [Fact]
    public async Task TicketAndMaintenance_RouteToDistinctWorktrees_WithoutTouchingPrimary()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.Tickets.CreateTicketAsync(fixture.Slug, "Root");
        var before = Git(fixture.Repository, "status", "--porcelain");

        var routes = await Task.WhenAll(
            fixture.Router.ResolveAsync(fixture.Slug, ticket.Id, [".agents/programmer/memory"]),
            fixture.Router.ResolveAsync(fixture.Slug, null, [".dashboard/summary"]));

        Assert.Equal(DurableWriteKind.Ticket, routes[0].Kind);
        Assert.Equal(DurableWriteKind.Maintenance, routes[1].Kind);
        Assert.NotEqual(routes[0].RootPath, routes[1].RootPath);
        Assert.Equal($"ticket/{ticket.Id}", routes[0].Branch);
        Assert.Equal($"maintenance/{fixture.Slug}", routes[1].Branch);
        Assert.Equal(before, Git(fixture.Repository, "status", "--porcelain"));
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, routes[1], "chore: release maintenance route");
    }

    [Fact]
    public async Task MaintenanceRoutes_AreSerializedUntilTheCurrentWriteIsValidated()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Router.ResolveAsync(fixture.Slug, null, [".dashboard/first"]);

        var secondTask = fixture.Router.ResolveAsync(fixture.Slug, null, [".dashboard/second"]);
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        await fixture.Router.CommitAndQueueAsync(fixture.Slug, first, "chore: complete first maintenance write");
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.RootPath, second.RootPath);
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, second, "chore: complete second maintenance write");
    }

    [Fact]
    public async Task ReusedMaintenanceWorktree_FastForwardsToTheCurrentIntegrationBranchBeforeWriting()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var first = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, first, "chore: initial no-op");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "new-baseline.txt"), "current");
        Git(fixture.Repository, "add", "new-baseline.txt");
        Git(fixture.Repository, "commit", "-m", "advance integration baseline");
        var expected = Git(fixture.Repository, "rev-parse", "integration").Trim();

        var second = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);

        Assert.Equal(expected, Git(second.RootPath, "rev-parse", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(second.RootPath, "new-baseline.txt")));
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, second, "chore: synchronized no-op");
        Assert.Equal(WorktreeMergeStatus.Completed,
            Assert.Single(await fixture.Queue!.ListAsync(fixture.Slug)).Status);
    }

    [Fact]
    public async Task SuccessfulNoOp_ReusesMaintenanceRowAndWorktreeWithoutAlert()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var first = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);

        var firstValidation = await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, first, "chore: no-op maintenance write");
        var firstRequest = Assert.Single(await fixture.Queue!.ListAsync(fixture.Slug));

        Assert.True(firstValidation.Status == DurableWriteValidationStatus.Ready,
            $"status={firstValidation.Status}; unexpected={string.Join(',', firstValidation.UnexpectedPaths)}; secrets={string.Join(',', firstValidation.SecretPaths)}; error={firstValidation.Error}");
        Assert.Equal(WorktreeMergeStatus.Completed, firstRequest.Status);
        Assert.Null(await fixture.Queue.GetAlertSummaryAsync(fixture.Slug));
        Assert.True(Directory.Exists(first.RootPath));

        var second = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);
        var secondValidation = await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, second, "chore: repeated no-op maintenance write");
        var reused = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(DurableWriteValidationStatus.Ready, secondValidation.Status);
        Assert.Equal(firstRequest.Id, reused.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, reused.Status);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Null(await fixture.Queue.GetAlertSummaryAsync(fixture.Slug));
    }

    [Fact]
    public async Task UnexpectedPath_ReturnsNeedsReview_AndLeavesFilesUnstaged()
    {
        using var fixture = await Fixture.CreateAsync();
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".dashboard/summary"]);
        Directory.CreateDirectory(Path.Combine(route.RootPath, ".dashboard", "summary"));
        await File.WriteAllTextAsync(Path.Combine(route.RootPath, ".dashboard", "summary", "output.md"), "ok");
        await File.WriteAllTextAsync(Path.Combine(route.RootPath, "unexpected.txt"), "keep");

        var result = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.NeedsReview, result.Status);
        Assert.Contains("unexpected.txt", result.UnexpectedPaths);
        Assert.Empty(Git(route.RootPath, "diff", "--cached", "--name-only"));
        Assert.True(File.Exists(Path.Combine(route.RootPath, "unexpected.txt")));
    }

    [Fact]
    public async Task ProbableSecret_BlocksBeforeAnythingIsStaged()
    {
        using var fixture = await Fixture.CreateAsync();
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".dashboard/summary"]);
        var directory = Path.Combine(route.RootPath, ".dashboard", "summary");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "output.md"), "api_key=abcdefgh12345678");

        var result = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.SecretBlocked, result.Status);
        Assert.Single(result.SecretPaths);
        Assert.Empty(Git(route.RootPath, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task DeclaredPathsOnly_AreStaged_AndLocalOnlyPathsAreRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".dashboard/summary"]);
        var directory = Path.Combine(route.RootPath, ".dashboard", "summary");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "output.md"), "safe content");

        var result = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.Ready, result.Status);
        Assert.Equal(".dashboard/summary/output.md", Git(route.RootPath, "diff", "--cached", "--name-only").Trim().Replace('\\', '/'));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/sessions"]));
    }

    [Fact]
    public async Task MaintenanceWrite_IsCommittedQueuedAndIntegratedAfterRestartSafeCheckpoint()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(route.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Durable memory\n");

        var validation = await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, route, "chore(memory): persist memory");
        var queued = await fixture.Queue!.GetAlertSummaryAsync(fixture.Slug);

        Assert.Equal(DurableWriteValidationStatus.Ready, validation.Status);
        Assert.Equal(WorktreeMergeStatus.Pending, queued!.MostSevereStatus);

        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, ".agents", "programmer", "memory", "MEMORY.md")));
        Assert.True(Directory.Exists(route.RootPath));
        Assert.Empty(Git(route.RootPath, "status", "--porcelain"));
        Assert.Null(await fixture.Queue.GetAlertSummaryAsync(fixture.Slug));
    }

    [Fact]
    public async Task CommittedMaintenanceWrite_IsReconciledWhenHostStopsBeforeQueueStateUpdate()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(route.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Committed before interruption\n");
        Git(route.RootPath, "add", ".agents/programmer/memory/MEMORY.md");
        Git(route.RootPath, "commit", "-m", "chore(memory): interrupted durable write");

        var restarted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);
        var integrated = await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        Assert.Equal(WorktreeMergeCheckpoint.Merge, integrated.Checkpoint);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, ".agents", "programmer", "memory", "MEMORY.md")));
        Assert.Null(await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None));
        fixture.Queue!.ReleaseMaintenanceWrite(route.QueueRequestId!.Value);
    }

    [Fact]
    public async Task NoOpMaintenanceWrite_RequeuesACommitCreatedAfterThePreviousIntegration()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var initial = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(initial.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Initial durable memory\n");
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, initial, "chore(memory): initial");
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue!.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);

        await File.AppendAllTextAsync(memory, "\nLate recovered lesson.\n");
        Git(initial.RootPath, "add", ".agents/programmer/memory/MEMORY.md");
        Git(initial.RootPath, "commit", "-m", "chore(memory): late recovered lesson");

        var noOp = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/memory"]);
        var validation = await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, noOp, "chore(memory): no-op checkpoint");
        var queued = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(DurableWriteValidationStatus.Ready, validation.Status);
        Assert.Equal(WorktreeMergeStatus.Pending, queued.Status);
        Assert.Contains("unintegrated maintenance commit", queued.Error, StringComparison.Ordinal);

        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        Assert.Contains("Late recovered lesson.",
            await File.ReadAllTextAsync(Path.Combine(fixture.Repository, ".agents", "programmer", "memory", "MEMORY.md")));
    }

    [Fact]
    public async Task ActiveMaintenanceWrite_IsNotMistakenForAnInterruptedWrite()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(route.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Still being written\n");

        Assert.Null(await fixture.Queue!.ProcessNextAsync(fixture.Slug, CancellationToken.None));
        var request = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        Assert.Equal(WorktreeMergeStatus.CommitPending, request.Status);
        Assert.True(File.Exists(memory));

        fixture.Queue.ReleaseMaintenanceWrite(route.QueueRequestId!.Value);
    }

    [Fact]
    public async Task UncommittedMaintenanceWrite_IsPreservedAndReportedAfterRestart()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(route.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Preserved after interruption\n");

        var restarted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);
        Assert.Null(await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None));

        var request = Assert.Single(await restarted.ListAsync(fixture.Slug));
        Assert.Equal(WorktreeMergeStatus.NeedsReview, request.Status);
        Assert.Contains("preserved uncommitted changes", request.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(memory));
        Assert.NotNull(await restarted.GetAlertSummaryAsync(fixture.Slug));
        fixture.Queue!.ReleaseMaintenanceWrite(route.QueueRequestId!.Value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _root;
        public string Repository { get; }
        public string Slug { get; }
        public TicketService Tickets { get; }
        public ProjectService Projects { get; }
        public TicketWorktreeService Worktrees { get; }
        public DurableWriteRouter Router { get; }
        public WorktreeMergeQueueService? Queue { get; }

        private Fixture(TempDir root, string repository, string slug, ProjectService projects,
            TicketService tickets, TicketWorktreeService worktrees, DurableWriteRouter router, WorktreeMergeQueueService? queue)
            => (_root, Repository, Slug, Projects, Tickets, Worktrees, Router, Queue) =
                (root, repository, slug, projects, tickets, worktrees, router, queue);

        public static async Task<Fixture> CreateAsync(bool withQueue = false)
        {
            var root = new TempDir();
            var repository = ProjectWorktreeSettingsTests.CreateRepository(root.Path, "integration");
            var projects = new ProjectService(Path.Combine(root.Path, "data"));
            var project = await projects.CreateProjectAsync("router");
            await projects.UpdateProjectAsync(project.Slug, repository);
            await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true, integrationBranch: "integration");
            var tickets = new TicketService(projects, new MemberService(projects));
            var worktrees = new TicketWorktreeService(projects, tickets);
            var queue = withQueue ? new WorktreeMergeQueueService(projects, worktrees) : null;
            return new Fixture(root, repository, project.Slug, projects, tickets, worktrees,
                new DurableWriteRouter(projects, worktrees, queue), queue);
        }
        public void Dispose() => _root.Dispose();
    }

    private static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error); return output;
    }
}
