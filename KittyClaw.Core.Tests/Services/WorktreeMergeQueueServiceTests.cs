using System.Diagnostics;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class WorktreeMergeQueueServiceTests
{
    [Fact]
    public async Task Success_FastForwardsThenProvesCommitBeforeCleanup()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(request.Id, result!.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.IntegratedCommit));
        Assert.Equal(0, Git(fixture.Repository, false, "merge-base", "--is-ancestor", result.IntegratedCommit!, "integration").ExitCode);
        Assert.False(Directory.Exists(request.WorktreePath));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}").ExitCode);
        var replay = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        Assert.Equal(request.Id, replay.Id);
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task NestedConfiguredRepository_IntegratesOnlyIntoConfiguredRepository()
    {
        using var fixture = await Fixture.CreateAsync(nested: true);
        var outerHead = Git(fixture.Workspace, true, "rev-parse", "HEAD").Output.Trim();
        var ticket = await fixture.CreateCommittedTicketAsync("nested.txt", "nested");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "nested.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "nested.txt")));
        Assert.Equal(outerHead, Git(fixture.Workspace, true, "rev-parse", "HEAD").Output.Trim());
    }

    [Fact]
    public async Task EnqueueAndProcess_AreIdempotentAndOrdered()
    {
        using var fixture = await Fixture.CreateAsync();
        var firstTicket = await fixture.CreateCommittedTicketAsync("first.txt", "first");
        var first = await fixture.Queue.EnqueueAsync(fixture.Slug, firstTicket, CancellationToken.None);
        var duplicate = await fixture.Queue.EnqueueAsync(fixture.Slug, firstTicket, CancellationToken.None);
        var secondTicket = await fixture.CreateCommittedTicketAsync("second.txt", "second");
        var second = await fixture.Queue.EnqueueAsync(fixture.Slug, secondTicket, CancellationToken.None);

        var firstResult = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        var secondResult = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.Id, firstResult!.Id);
        Assert.Equal(second.Id, secondResult!.Id);
        Assert.Equal(2, (await fixture.Queue.ListAsync(fixture.Slug)).Count);
    }

    [Fact]
    public async Task DirtyCheckout_IsPreservedAndClassified()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "keep.txt"), "keep");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.BlockedByExternalChanges, result!.Status);
        Assert.Contains("local changes", result.Error);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "keep.txt")));
        Assert.True(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task DirtyTargetCheckout_BecomesVisibleAndResumesAfterExternalChangesAreResolved()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var external = Path.Combine(fixture.Repository, "external.txt");
        await File.WriteAllTextAsync(external, "external");

        var blocked = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        var summary = await fixture.Queue.GetAlertSummaryAsync(fixture.Slug);

        Assert.Equal(WorktreeMergeStatus.BlockedByExternalChanges, blocked!.Status);
        Assert.Equal(1, summary!.ActiveCount);
        Assert.Equal(WorktreeMergeStatus.BlockedByExternalChanges, summary.MostSevereStatus);
        File.Delete(external);

        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(request.Id, completed!.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task StagedTicketWrite_IsCommittedAndIntegrated()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("committed.txt", "committed");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var staged = Path.Combine(request.WorktreePath, "staged.txt");
        await File.WriteAllTextAsync(staged, "preserve");
        Git(request.WorktreePath, true, "add", "staged.txt");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "staged.txt")));
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task DurableMemoryAndRecognizedTemporaryFiles_AreFinalizedWithoutLoss()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var memory = Path.Combine(request.WorktreePath, ".agents", "programmer", "memory", "lesson.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "durable lesson");
        await File.WriteAllTextAsync(Path.Combine(request.WorktreePath, "scratch.tmp"), "temporary");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal("durable lesson", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, ".agents", "programmer", "memory", "lesson.md")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "scratch.tmp")));
    }

    [Fact]
    public async Task PotentiallySensitiveFile_BlocksCleanupAndIsPreserved()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var secret = Path.Combine(request.WorktreePath, ".env");
        await File.WriteAllTextAsync(secret, "TOKEN=keep-me");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.NeedsReview, result!.Status);
        Assert.Contains("potentially sensitive", result.Error);
        Assert.True(File.Exists(secret));
        Assert.True(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task UnexpectedUntrackedFile_RequiresReviewWithoutCommitAndResumesAfterRemoval()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var headBeforeReview = Git(request.WorktreePath, true, "rev-parse", "HEAD").Output.Trim();
        var unexpected = Path.Combine(request.WorktreePath, "keep.txt");
        await File.WriteAllTextAsync(unexpected, "preserve for review");

        var review = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.NeedsReview, review!.Status);
        Assert.Contains("unexpected untracked path", review.Error);
        Assert.Equal(headBeforeReview, Git(request.WorktreePath, true, "rev-parse", "HEAD").Output.Trim());
        Assert.Contains("?? keep.txt", Git(request.WorktreePath, true, "status", "--short").Output);
        Assert.True(File.Exists(unexpected));
        Assert.True(Directory.Exists(request.WorktreePath));
        Assert.Equal(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}").ExitCode);

        File.Delete(unexpected);
        var completed = await fixture.Queue.ResumeAsync(fixture.Slug, request.Id, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.False(Directory.Exists(request.WorktreePath));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}").ExitCode);
    }

    [Fact]
    public async Task StartupRecovery_QueuesGhostWorktreeForTerminalRootTicket()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("terminal.txt", "terminal");
        await fixture.Tickets.MoveTicketAsync(fixture.Slug, ticket, "Done", "test");

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var queued = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(1, recovered);
        Assert.Equal(ticket, queued.RootTicketId);
        Assert.Equal(WorktreeMergeStatus.Pending, queued.Status);
    }

    [Fact]
    public async Task Recovery_WaitsUntilProcessorReleasesTheTicketWorktree()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("terminal.txt", "terminal");
        await fixture.Tickets.MoveTicketAsync(fixture.Slug, ticket, "Done", "test");
        var coordinator = new WorktreeFinalizationCoordinator();
        var queue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees, coordinator);

        using (coordinator.Enter(fixture.Slug, ticket))
            Assert.Equal(0, await queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None));
        Assert.Empty(await queue.ListAsync(fixture.Slug));

        Assert.Equal(1, await queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None));
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await queue.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Conflict_IsPersistedAndCanBeResumedAfterResolution()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "target\n");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "target change");
        var ticket = await fixture.CreateTicketAsync();
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        // Reset the feature branch to the common base, then create a conflicting edit.
        Git(worktree.Path, true, "reset", "--hard", "HEAD~1");
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "shared.txt"), "source\n");
        Git(worktree.Path, true, "add", "shared.txt");
        Git(worktree.Path, true, "commit", "-m", "source change");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);

        var conflict = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Conflict, conflict!.Status);
        Assert.Contains("shared.txt", conflict.ConflictFiles);
        Assert.True(Directory.Exists(request.WorktreePath));
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "shared.txt"), "resolved\n");
        Git(worktree.Path, true, "add", "shared.txt");

        var completed = await fixture.Queue.ResumeAsync(fixture.Slug, request.Id, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal("resolved\n", (await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ProcessingRow_IsRecoveredAfterServiceRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("restart.txt", "restart");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = 1 WHERE Id = {request.Id}");
        var restarted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);

        var result = await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
    }

    [Theory]
    [InlineData(WorktreeMergeCheckpoint.Preparation)]
    [InlineData(WorktreeMergeCheckpoint.Writing)]
    [InlineData(WorktreeMergeCheckpoint.Validation)]
    [InlineData(WorktreeMergeCheckpoint.Commit)]
    [InlineData(WorktreeMergeCheckpoint.Waiting)]
    [InlineData(WorktreeMergeCheckpoint.Rebase)]
    [InlineData(WorktreeMergeCheckpoint.Merge)]
    public async Task ProcessingCheckpoint_IsRecoveredIdempotentlyAfterRestart(WorktreeMergeCheckpoint checkpoint)
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync($"{checkpoint}.txt", checkpoint.ToString());
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = 1, Checkpoint = {(int)checkpoint} WHERE Id = {request.Id}");

        var restarted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);
        var result = await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal(WorktreeMergeCheckpoint.Merge, result.Checkpoint);
        Assert.NotNull(result.LocalIntegratedAt);
        Assert.Null(result.RemotePublishedAt);
        Assert.Null(await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None));
    }

    [Theory]
    [InlineData(WorktreeMergeCheckpoint.Rebase)]
    [InlineData(WorktreeMergeCheckpoint.Merge)]
    public async Task CompletedGitSideEffect_IsReconciledAfterRestart(WorktreeMergeCheckpoint checkpoint)
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync($"{checkpoint}.txt", checkpoint.ToString());
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "target-advance.txt"), "advance");
        Git(fixture.Repository, true, "add", "target-advance.txt");
        Git(fixture.Repository, true, "commit", "-m", "advance integration target");
        Git(request.WorktreePath, true, "rebase", "integration");
        if (checkpoint == WorktreeMergeCheckpoint.Merge)
            Git(fixture.Repository, true, "merge", "--ff-only", request.SourceBranch);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = 1, Checkpoint = {(int)checkpoint} WHERE Id = {request.Id}");

        var restarted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);
        var result = await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal(0, Git(fixture.Repository, false, "merge-base", "--is-ancestor", result.IntegratedCommit!, "integration").ExitCode);
        Assert.False(Directory.Exists(request.WorktreePath));
        Assert.Null(await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None));
    }

    [Fact]
    public async Task RemotePublication_IsRecordedSeparatelyFromLocalIntegration()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("publish.txt", "publish");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.NotNull(integrated!.LocalIntegratedAt);
        Assert.Null(integrated.RemotePublishedAt);
        await fixture.Queue.MarkPublishedAsync(fixture.Slug, request.Id);

        var published = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        Assert.NotNull(published.RemotePublishedAt);
    }

    private sealed class Fixture : IDisposable
    {
        public TempDir Root { get; }
        public string Workspace { get; }
        public string Repository { get; }
        public string Slug { get; }
        public ProjectService Projects { get; }
        public TicketService Tickets { get; }
        public TicketWorktreeService Worktrees { get; }
        public WorktreeMergeQueueService Queue { get; }

        private Fixture(TempDir root, string workspace, string repository, string slug, ProjectService projects,
            TicketService tickets, TicketWorktreeService worktrees)
        {
            Root = root; Workspace = workspace; Repository = repository; Slug = slug; Projects = projects; Tickets = tickets; Worktrees = worktrees;
            Queue = new WorktreeMergeQueueService(projects, worktrees);
        }

        public static async Task<Fixture> CreateAsync(bool nested = false)
        {
            var root = new TempDir();
            var workspace = ProjectWorktreeSettingsTests.CreateRepository(root.Path, nested ? "outer" : "integration");
            var repository = nested
                ? ProjectWorktreeSettingsTests.CreateRepository(workspace, "integration")
                : workspace;
            var projects = new ProjectService(Path.Combine(root.Path, "data"));
            var project = await projects.CreateProjectAsync("merge-queue");
            await projects.UpdateProjectAsync(project.Slug, workspace);
            await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true, integrationBranch: "integration",
                repositoryPath: nested ? Path.GetRelativePath(workspace, repository) : null);
            var tickets = new TicketService(projects, new MemberService(projects));
            var worktrees = new TicketWorktreeService(projects, tickets);
            return new Fixture(root, workspace, repository, project.Slug, projects, tickets, worktrees);
        }

        public async Task<int> CreateTicketAsync() => (await Tickets.CreateTicketAsync(Slug, "Merge candidate")).Id;

        public async Task<int> CreateCommittedTicketAsync(string file, string content)
        {
            var ticket = await CreateTicketAsync();
            var worktree = (await Worktrees.ResolveAsync(Slug, ticket, CancellationToken.None))!;
            await File.WriteAllTextAsync(Path.Combine(worktree.Path, file), content);
            Git(worktree.Path, true, "add", file);
            Git(worktree.Path, true, "commit", "-m", content);
            return ticket;
        }

        public void Dispose() => Root.Dispose();
    }

    private static (int ExitCode, string Output, string Error) Git(string path, bool success, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (success) Assert.True(process.ExitCode == 0, error);
        return (process.ExitCode, output, error);
    }
}
