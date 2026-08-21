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
    public async Task RecoveryAfterWorktreesAreDisabled_FinalizesSafeTerminalWorktree()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("terminal.txt", "terminal");
        await fixture.Tickets.MoveTicketAsync(fixture.Slug, ticket, "Done", "test");
        await fixture.Projects.UpdateProjectAsync(fixture.Slug, null, worktreesEnabled: false);

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "terminal.txt")));
        Assert.False(Directory.Exists(completed.WorktreePath));
    }

    [Fact]
    public async Task RecoveryAfterWorktreesAreDisabled_PreservesUnsafeTerminalAndIgnoresNonTerminalWorktree()
    {
        using var fixture = await Fixture.CreateAsync();
        var terminalTicket = await fixture.CreateCommittedTicketAsync("terminal.txt", "terminal");
        var terminalWorktree = (await fixture.Worktrees.InspectAsync(fixture.Slug, terminalTicket))!;
        await File.WriteAllTextAsync(Path.Combine(terminalWorktree.Path, "keep.txt"), "unexpected");
        await fixture.Tickets.MoveTicketAsync(fixture.Slug, terminalTicket, "Done", "test");
        var nonTerminalTicket = await fixture.CreateCommittedTicketAsync("active.txt", "active");
        var nonTerminalWorktree = (await fixture.Worktrees.InspectAsync(fixture.Slug, nonTerminalTicket))!;
        await fixture.Projects.UpdateProjectAsync(fixture.Slug, null, worktreesEnabled: false);

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var review = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(WorktreeMergeStatus.NeedsReview, review!.Status);
        Assert.True(File.Exists(Path.Combine(terminalWorktree.Path, "keep.txt")));
        Assert.True(Directory.Exists(terminalWorktree.Path));
        Assert.True(Directory.Exists(nonTerminalWorktree.Path));
        Assert.DoesNotContain(await fixture.Queue.ListAsync(fixture.Slug), row => row.RootTicketId == nonTerminalTicket);
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
    public async Task Conflict_CanBeResumedAfterOperatorAlreadyCompletedTheRebase()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "target\n");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "target change");
        var ticket = await fixture.CreateTicketAsync();
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        Git(worktree.Path, true, "reset", "--hard", "HEAD~1");
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "shared.txt"), "source\n");
        Git(worktree.Path, true, "add", "shared.txt");
        Git(worktree.Path, true, "commit", "-m", "source change");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);

        var conflict = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Conflict, conflict!.Status);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "shared.txt"), "resolved\n");
        Git(worktree.Path, true, "add", "shared.txt");
        Git(worktree.Path, true, "-c", "core.editor=true", "rebase", "--continue");
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue
                SET Status = {(int)WorktreeMergeStatus.Failed}, Checkpoint = {(int)WorktreeMergeCheckpoint.Rebase}
                WHERE Id = {request.Id}
                """);

        var completed = await fixture.Queue.ResumeAsync(fixture.Slug, request.Id, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal("resolved\n", (await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task SourceThatAlreadyMergedTarget_IsFastForwardedWithoutReplayingOldConflicts()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "base\n");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "common base");
        var ticket = await fixture.CreateTicketAsync();
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "shared.txt"), "source\n");
        Git(worktree.Path, true, "add", "shared.txt");
        Git(worktree.Path, true, "commit", "-m", "source change");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "target\n");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "target change");
        Git(worktree.Path, false, "merge", "integration", "--no-edit");
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "shared.txt"), "resolved merge\n");
        Git(worktree.Path, true, "add", "shared.txt");
        Git(worktree.Path, true, "-c", "core.editor=true", "commit", "--no-edit");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);

        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal("resolved merge\n", (await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"))).Replace("\r\n", "\n"));
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task UnresolvedMergeConflict_IsPreservedWithoutFinalizationCommit()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "base");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "shared base");
        var ticket = await fixture.CreateCommittedTicketAsync("shared.txt", "ticket version");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "target version");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "target version");
        var headBeforeReview = Git(request.WorktreePath, true, "rev-parse", "HEAD").Output.Trim();
        Assert.NotEqual(0, Git(request.WorktreePath, false, "merge", "integration").ExitCode);

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Conflict, result!.Status);
        Assert.Contains("shared.txt", result.ConflictFiles);
        Assert.Contains("no finalization commit", result.Error);
        Assert.Equal(headBeforeReview, Git(request.WorktreePath, true, "rev-parse", "HEAD").Output.Trim());
        Assert.True(Directory.Exists(request.WorktreePath));
        Assert.Contains("UU shared.txt", Git(request.WorktreePath, true, "status", "--short").Output);
    }

    [Fact]
    public async Task StagedConflictMarkers_RequireReviewWithoutFinalizationCommit()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var conflicted = Path.Combine(request.WorktreePath, "conflicted.txt");
        await File.WriteAllTextAsync(conflicted, "<<<<<<< HEAD\ncurrent\n=======\nincoming\n>>>>>>> branch\n");
        Git(request.WorktreePath, true, "add", "conflicted.txt");
        var headBeforeReview = Git(request.WorktreePath, true, "rev-parse", "HEAD").Output.Trim();

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.NeedsReview, result!.Status);
        Assert.Contains("conflict markers", result.Error);
        Assert.Contains("conflicted.txt", result.ConflictFiles);
        Assert.Equal(headBeforeReview, Git(request.WorktreePath, true, "rev-parse", "HEAD").Output.Trim());
        Assert.True(File.Exists(conflicted));
        Assert.True(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task CompletedRequest_WithNewCommittedWorktree_EnqueuesAnotherGeneration()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var first = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "memory.md"), "durable lesson");
        Git(worktree.Path, true, "add", "memory.md");
        Git(worktree.Path, true, "commit", "-m", "record durable lesson");

        var second = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(WorktreeMergeStatus.Pending, second.Status);
        Assert.Equal(second.Id, completed!.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
        Assert.Equal("durable lesson", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "memory.md")));
        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task CompletedRequest_WithNewDirtyWorktree_EnqueuesAndFinalizesChanges()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var first = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        var relativeMemory = Path.Combine(".agents", "processors", "column-1", "memory", "MEMORY.md");
        var memoryPath = Path.Combine(worktree.Path, relativeMemory);
        Directory.CreateDirectory(Path.GetDirectoryName(memoryPath)!);
        await File.WriteAllTextAsync(memoryPath, "preserve me");

        var second = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(WorktreeMergeStatus.Pending, second.Status);
        Assert.Equal(second.Id, completed!.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, relativeMemory)));
        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task IntegratedTicketBranch_IsDeletedWhenItsTrackedRemoteRefIsStale()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("tracked.txt", "tracked branch");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var staleUpstream = Git(request.WorktreePath, true, "rev-parse", "HEAD~1").Output.Trim();
        Git(fixture.Repository, true, "update-ref", $"refs/remotes/origin/{request.SourceBranch}", staleUpstream);
        Git(fixture.Repository, true, "config", $"branch.{request.SourceBranch}.remote", "origin");
        Git(fixture.Repository, true, "config", $"branch.{request.SourceBranch}.merge", $"refs/heads/{request.SourceBranch}");

        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}").ExitCode);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "tracked.txt")));
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

    [Fact]
    public async Task MaintenanceResume_UsesCommitCreatedAfterTheRequestWasPrepared()
    {
        using var fixture = await Fixture.CreateAsync();
        var worktreePath = Path.Combine(fixture.Root.Path, "maintenance-worktree");
        Git(fixture.Repository, true, "worktree", "add", "-b", "maintenance/test", worktreePath, "integration");
        var request = await fixture.Queue.PrepareMaintenanceAsync(
            fixture.Slug, worktreePath, "maintenance/test", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "maintenance.txt"), "late commit");
        Git(worktreePath, true, "add", "maintenance.txt");
        Git(worktreePath, true, "commit", "-m", "late maintenance commit");
        var actualHead = Git(worktreePath, true, "rev-parse", "HEAD").Output.Trim();
        fixture.Queue.ReleaseMaintenanceWrite(request.Id);

        var completed = await fixture.Queue.ResumeAsync(fixture.Slug, request.Id, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal(actualHead, completed.IntegratedCommit);
        Assert.Equal("late commit", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "maintenance.txt")));
    }

    [Fact]
    public async Task TargetAdvanceBetweenRebaseAndFastForward_IsRebasedAgainWithinTheSameAttempt()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("candidate.txt", "candidate");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var advanced = false;
        var queue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees, beforeFastForward: (repository, _) =>
        {
            if (advanced) return;
            advanced = true;
            File.WriteAllText(Path.Combine(repository, "concurrent.txt"), "target advance");
            Git(repository, true, "add", "concurrent.txt");
            Git(repository, true, "commit", "-m", "advance target during integration");
        });

        var completed = await queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.True(advanced);
        Assert.Equal(request.Id, completed!.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "candidate.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "concurrent.txt")));
    }

    [Fact]
    public async Task ResumeAfterPartialCleanup_CompletesWhenCommitIsIntegratedAndOnlyEmptyFolderRemains()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("integrated.txt", "integrated");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var first = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed, first!.Status);
        Directory.CreateDirectory(request.WorktreePath);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = 3, Error = 'simulated partial cleanup' WHERE Id = {request.Id}");

        var resumed = await fixture.Queue.ResumeAsync(fixture.Slug, request.Id, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, resumed!.Status);
        Assert.False(Directory.Exists(request.WorktreePath));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}").ExitCode);
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
