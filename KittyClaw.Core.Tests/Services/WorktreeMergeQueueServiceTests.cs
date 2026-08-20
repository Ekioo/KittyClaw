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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirtyCheckoutOrWorktree_IsPreservedAndClassified(bool dirtyCheckout)
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var dirtyPath = dirtyCheckout ? fixture.Repository : request.WorktreePath;
        await File.WriteAllTextAsync(Path.Combine(dirtyPath, "keep.txt"), "keep");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(dirtyCheckout ? WorktreeMergeStatus.BlockedByExternalChanges : WorktreeMergeStatus.NeedsReview, result!.Status);
        Assert.Contains(dirtyCheckout ? "local changes" : "unvalidated", result.Error);
        Assert.True(File.Exists(Path.Combine(dirtyPath, "keep.txt")));
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
    public async Task StagedTicketWrite_IsCommitPendingAndPreserved()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("committed.txt", "committed");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var staged = Path.Combine(request.WorktreePath, "staged.txt");
        await File.WriteAllTextAsync(staged, "preserve");
        Git(request.WorktreePath, true, "add", "staged.txt");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.CommitPending, result!.Status);
        Assert.Equal(WorktreeMergeCheckpoint.Commit, result.Checkpoint);
        Assert.True(File.Exists(staged));
        Assert.Equal("staged.txt", Git(request.WorktreePath, true, "diff", "--cached", "--name-only").Output.Trim());
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
