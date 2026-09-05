using System.Diagnostics;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class WorktreeMergeQueueServiceTests
{
    [Fact]
    public async Task GetAsync_ReturnsOneRequestAndItsSynchronizationCheckpoint()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var queued = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        var request = await fixture.Queue.GetAsync(fixture.Slug, queued.Id);

        Assert.NotNull(request);
        Assert.Equal(WorktreeMergeStatus.Completed, request.Status);
        Assert.Equal(LocalCheckoutSyncStatus.Completed, request.SyncStatus);
        Assert.False(request.HasSynchronizationLag);
        Assert.False(string.IsNullOrWhiteSpace(request.IntegratedCommit));
        Assert.Equal(request.IntegratedCommit, request.SyncTargetCommit);
        Assert.Null(await fixture.Queue.GetAsync(fixture.Slug, long.MaxValue));
    }

    [Fact]
    public async Task LegacyDuplicateActiveRows_ArePreservedWithoutBlockingMigration()
    {
        using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE WorktreeMergeQueue (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TicketId INTEGER NOT NULL,
                    RootTicketId INTEGER NOT NULL,
                    WorktreePath TEXT NOT NULL,
                    SourceBranch TEXT NOT NULL,
                    TargetBranch TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    SourceCommit TEXT NULL,
                    IntegratedCommit TEXT NULL,
                    Error TEXT NULL,
                    ConflictFiles TEXT NULL
                );
                INSERT INTO WorktreeMergeQueue
                    (TicketId, RootTicketId, WorktreePath, SourceBranch, TargetBranch, Status, CreatedAt, UpdatedAt)
                VALUES
                    (41, 41, 'old', 'old-branch', 'integration', 5, '2026-01-01', '2026-01-01'),
                    (41, 41, 'new', 'new-branch', 'integration', 5, '2026-01-02', '2026-01-02');
                """);
        }

        var rows = await fixture.Queue.ListAsync(fixture.Slug);

        Assert.Equal(2, rows.Count);
        Assert.Equal(WorktreeMergeStatus.Completed, rows[0].Status);
        Assert.Contains("Superseded duplicate integration", rows[0].Error);
        Assert.Equal(WorktreeMergeStatus.CommitPending, rows[1].Status);
    }

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
        Assert.True(TipContains(fixture.Repository, result.IntegratedCommit!));
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
        Assert.True(ExistsAtTip(fixture.Repository, "nested.txt"));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "nested.txt")));
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
    public async Task DirtyPrimaryCheckout_NoLongerBlocksIntegrationAndIsPreserved()
    {
        using var fixture = await Fixture.CreateAsync();
        var tracked = Path.Combine(fixture.Repository, "tracked.txt");
        await File.WriteAllTextAsync(tracked, "committed");
        Git(fixture.Repository, true, "add", "tracked.txt");
        Git(fixture.Repository, true, "commit", "-m", "tracked baseline");
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await File.WriteAllTextAsync(tracked, "local modification");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "staged.txt"), "staged");
        Git(fixture.Repository, true, "add", "staged.txt");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "keep.txt"), "keep");
        var before = CheckoutFingerprint(fixture.Repository);

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.True(TipContains(fixture.Repository, result.IntegratedCommit!));
        Assert.True(ExistsAtTip(fixture.Repository, "feature.txt"));
        Assert.Equal(before, CheckoutFingerprint(fixture.Repository));
        Assert.Equal("local modification", await File.ReadAllTextAsync(tracked));
        Assert.Equal("staged", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "staged.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "keep.txt")));
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task DirtyPrimaryCheckout_DoesNotPreventCheckpointingSafeTicketFiles()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var isolated = Path.Combine(request.WorktreePath, "recovered.txt");
        await File.WriteAllTextAsync(isolated, "recovered");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "external.txt"), "external");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal("recovered", ShowAtTip(fixture.Repository, "recovered.txt").Trim());
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "recovered.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "external.txt")));
    }

    [Fact]
    public async Task DivergedTargetBranch_BecomesVisibleAndResumesAfterReconciliation()
    {
        using var fixture = await Fixture.CreateAsync();
        var firstTicket = await fixture.CreateCommittedTicketAsync("first.txt", "first");
        await fixture.Queue.EnqueueAsync(fixture.Slug, firstTicket, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "external.txt"), "external");
        Git(fixture.Repository, true, "add", "external.txt");
        Git(fixture.Repository, true, "commit", "-m", "external target commit");
        var secondTicket = await fixture.CreateCommittedTicketAsync("second.txt", "second");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, secondTicket, CancellationToken.None);

        var blocked = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        var summary = await fixture.Queue.GetAlertSummaryAsync(fixture.Slug);

        Assert.Equal(WorktreeMergeStatus.BlockedByExternalChanges, blocked!.Status);
        Assert.Contains("divergence", blocked.Error);
        Assert.Equal(1, summary!.ActiveCount);
        Assert.Equal(WorktreeMergeStatus.BlockedByExternalChanges, summary.MostSevereStatus);
        Git(fixture.Repository, true, "merge", "--no-edit", TipRef);

        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(request.Id, completed!.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
        Assert.True(ExistsAtTip(fixture.Repository, "first.txt"));
        Assert.True(ExistsAtTip(fixture.Repository, "second.txt"));
        Assert.True(ExistsAtTip(fixture.Repository, "external.txt"));
    }

    [Fact]
    public async Task AlreadyIntegratedCleanWorktree_IsCleanedUpDespiteDirtyTargetCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        Git(fixture.Repository, true, "merge", "--ff-only", request.SourceBranch);
        var external = Path.Combine(fixture.Repository, "external.txt");
        await File.WriteAllTextAsync(external, "external");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal(0, Git(fixture.Repository, false, "merge-base", "--is-ancestor", result.IntegratedCommit!, "integration").ExitCode);
        Assert.False(Directory.Exists(request.WorktreePath));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}").ExitCode);
        Assert.Equal("external", await File.ReadAllTextAsync(external));
        Assert.Equal("?? external.txt", Git(fixture.Repository, true, "status", "--porcelain").Output.Trim());
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
        Assert.Equal("preserve", ShowAtTip(fixture.Repository, "staged.txt"));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "staged.txt")));
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
        Assert.Equal("durable lesson", ShowAtTip(fixture.Repository, ".agents/programmer/memory/lesson.md"));
        Assert.False(ExistsAtTip(fixture.Repository, "scratch.tmp"));
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
    public async Task CodeVariableNamedLikeASecret_IsFinalizedAndIntegrated()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var script = Path.Combine(request.WorktreePath, "reauth.mjs");
        await File.WriteAllTextAsync(script,
            "const body = { client_secret: clientSecretV2, access_token: accessToken };\n");

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal("const body = { client_secret: clientSecretV2, access_token: accessToken };",
            ShowAtTip(fixture.Repository, "reauth.mjs").TrimEnd());
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Theory]
    [InlineData("client_secret: \"literalSecretValue\"")]
    [InlineData("access_token=abcdefgh12345678")]
    public async Task LiteralAndProbableTokenValues_BlockFinalization(string content)
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var script = Path.Combine(request.WorktreePath, "reauth.mjs");
        await File.WriteAllTextAsync(script, content);

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.NeedsReview, result!.Status);
        Assert.Contains("possible secret content", result.Error);
        Assert.True(File.Exists(script));
        Assert.True(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task SafeUntrackedFile_IsCheckpointedIntegratedAndCleanedUp()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var unexpected = Path.Combine(request.WorktreePath, "keep.txt");
        await File.WriteAllTextAsync(unexpected, "preserve for review");

        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal("preserve for review", ShowAtTip(fixture.Repository, "keep.txt"));
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
    public async Task StartupRecovery_CleansRegisteredWorktreeLeftAfterCompletedIntegration()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("terminal.txt", "terminal");
        var first = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
        var leftBehind = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await fixture.Tickets.MoveTicketAsync(fixture.Slug, ticket, "Done", "test");

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var pending = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(first.Id, pending.Id);
        Assert.Equal(WorktreeMergeStatus.Pending, pending.Status);
        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.False(Directory.Exists(leftBehind.Path));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet",
            $"refs/heads/{leftBehind.Branch}").ExitCode);
    }

    [Fact]
    public async Task StartupRecovery_IntegratesRegisteredLegacyChildWorktreeForTerminalRoot()
    {
        using var fixture = await Fixture.CreateAsync();
        var root = await fixture.Tickets.CreateTicketAsync(fixture.Slug, "Root ticket");
        var child = await fixture.Tickets.CreateTicketAsync(fixture.Slug, "Legacy child", parentId: root.Id);
        var worktreesDirectory = Path.Combine(Path.GetDirectoryName(fixture.Repository)!,
            $"{Path.GetFileName(fixture.Repository)}.worktrees");
        var legacyPath = Path.Combine(worktreesDirectory, $"ticket-{child.Id}");
        Directory.CreateDirectory(worktreesDirectory);
        Git(fixture.Repository, true, "worktree", "add", "-b", $"ticket/{child.Id}", legacyPath, "integration");
        await File.WriteAllTextAsync(Path.Combine(legacyPath, "legacy-child.txt"), "preserve me");
        Git(legacyPath, true, "add", "legacy-child.txt");
        Git(legacyPath, true, "commit", "-m", "legacy child delivery");
        await fixture.Tickets.MoveTicketAsync(fixture.Slug, root.Id, "Done", "test");

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var pending = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(root.Id, pending.RootTicketId);
        Assert.Equal(child.Id, pending.TicketId);
        Assert.Equal(Path.GetFullPath(legacyPath), Path.GetFullPath(pending.WorktreePath));
        Assert.Equal($"ticket/{child.Id}", pending.SourceBranch);
        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal("preserve me", ShowAtTip(fixture.Repository, "legacy-child.txt"));
        Assert.False(Directory.Exists(legacyPath));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet",
            $"refs/heads/ticket/{child.Id}").ExitCode);
    }

    [Fact]
    public async Task StartupRecovery_RepeatedRestartsStayIdempotent()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("terminal.txt", "terminal");
        var first = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
        var leftBehind = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await fixture.Tickets.MoveTicketAsync(fixture.Slug, ticket, "Done", "test");

        await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var requeued = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var afterRestart = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        var recoveredAfterCleanup =
            await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var final = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(first.Id, requeued.Id);
        Assert.Equal(WorktreeMergeStatus.Pending, requeued.Status);
        Assert.Equal(requeued, afterRestart);
        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal(0, recoveredAfterCleanup);
        Assert.Equal(WorktreeMergeStatus.Completed, final.Status);
        Assert.False(Directory.Exists(leftBehind.Path));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet",
            $"refs/heads/{leftBehind.Branch}").ExitCode);
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
        Assert.True(ExistsAtTip(fixture.Repository, "terminal.txt"));
        Assert.False(Directory.Exists(completed.WorktreePath));
    }

    [Fact]
    public async Task RecoveryAfterWorktreesAreDisabled_RecoversSafeTerminalAndIgnoresNonTerminalWorktree()
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
        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal("unexpected", ShowAtTip(fixture.Repository, "keep.txt"));
        Assert.False(Directory.Exists(terminalWorktree.Path));
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
    public async Task BusyTicket_DoesNotBlockTheNextIndependentIntegration()
    {
        using var fixture = await Fixture.CreateAsync();
        var firstTicket = await fixture.CreateCommittedTicketAsync("first.txt", "first");
        var secondTicket = await fixture.CreateCommittedTicketAsync("second.txt", "second");
        var first = await fixture.Queue.EnqueueAsync(fixture.Slug, firstTicket, CancellationToken.None);
        var second = await fixture.Queue.EnqueueAsync(fixture.Slug, secondTicket, CancellationToken.None);
        var coordinator = new WorktreeFinalizationCoordinator();
        var queue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees, coordinator);

        WorktreeMergeRequest? completed;
        using (coordinator.Enter(fixture.Slug, first.RootTicketId))
            completed = await queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(second.Id, completed!.Id);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
        Assert.Equal(WorktreeMergeStatus.Pending,
            (await queue.ListAsync(fixture.Slug)).Single(row => row.Id == first.Id).Status);
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
        Assert.Equal("resolved\n", ShowAtTip(fixture.Repository, "shared.txt").Replace("\r\n", "\n"));
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
        Assert.Equal("resolved\n", ShowAtTip(fixture.Repository, "shared.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task AppendOnlyProcessorMemoryConflict_IsUnionMergedAndIntegrated()
    {
        using var fixture = await Fixture.CreateAsync();
        var relativePath = Path.Combine(".agents", "processors", "column-12", "memory", "MEMORY.md");
        var targetPath = Path.Combine(fixture.Repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "# Mémoire — été\n");
        Git(fixture.Repository, true, "add", relativePath);
        Git(fixture.Repository, true, "commit", "-m", "memory base");

        var ticket = await fixture.CreateTicketAsync();
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await File.AppendAllTextAsync(Path.Combine(worktree.Path, relativePath), "\n- leçon ticket : déjà vérifiée\n");
        Git(worktree.Path, true, "add", relativePath);
        Git(worktree.Path, true, "commit", "-m", "ticket memory");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);

        await File.AppendAllTextAsync(targetPath, "\n- leçon cible : intégrité préservée\n");
        Git(fixture.Repository, true, "add", relativePath);
        Git(fixture.Repository, true, "commit", "-m", "target memory");

        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        var merged = ShowAtTip(fixture.Repository, relativePath);
        Assert.Contains("- leçon ticket : déjà vérifiée", merged);
        Assert.Contains("- leçon cible : intégrité préservée", merged);
        Assert.DoesNotContain("<<<<<<<", merged);
        Assert.True(merged.Length < 1024);
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task ExistingAppendOnlyProcessorMemoryConflict_IsResolvedOnResume()
    {
        using var fixture = await Fixture.CreateAsync();
        var relativePath = Path.Combine(".agents", "processors", "column-12", "memory", "MEMORY.md");
        var targetPath = Path.Combine(fixture.Repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "# Memory\n");
        Git(fixture.Repository, true, "add", relativePath);
        Git(fixture.Repository, true, "commit", "-m", "memory base");
        var ticket = await fixture.CreateTicketAsync();
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await File.AppendAllTextAsync(Path.Combine(worktree.Path, relativePath), "\n- ticket lesson\n");
        Git(worktree.Path, true, "add", relativePath);
        Git(worktree.Path, true, "commit", "-m", "ticket memory");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await File.AppendAllTextAsync(targetPath, "\n- target lesson\n");
        Git(fixture.Repository, true, "add", relativePath);
        Git(fixture.Repository, true, "commit", "-m", "target memory");
        Assert.NotEqual(0, Git(worktree.Path, false, "rebase", "integration").ExitCode);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Conflict}, Checkpoint = {(int)WorktreeMergeCheckpoint.Rebase} WHERE Id = {request.Id}");

        var completed = await fixture.Queue.ResumeAsync(fixture.Slug, request.Id, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        var merged = ShowAtTip(fixture.Repository, relativePath);
        Assert.Contains("- ticket lesson", merged);
        Assert.Contains("- target lesson", merged);
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task ExistingMemoryConflict_CatchesUpWhenTargetAdvancedWhileWaiting()
    {
        using var fixture = await Fixture.CreateAsync();
        var relativePath = Path.Combine(".agents", "processors", "column-12", "memory", "MEMORY.md");
        var targetPath = Path.Combine(fixture.Repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "# Memory\n");
        Git(fixture.Repository, true, "add", relativePath);
        Git(fixture.Repository, true, "commit", "-m", "memory base");
        var ticket = await fixture.CreateTicketAsync();
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await File.AppendAllTextAsync(Path.Combine(worktree.Path, relativePath), "\n- ticket lesson\n");
        Git(worktree.Path, true, "add", relativePath);
        Git(worktree.Path, true, "commit", "-m", "ticket memory");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await File.AppendAllTextAsync(targetPath, "\n- target lesson\n");
        Git(fixture.Repository, true, "add", relativePath);
        Git(fixture.Repository, true, "commit", "-m", "target memory");
        Assert.NotEqual(0, Git(worktree.Path, false, "rebase", "integration").ExitCode);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "later.txt"), "later target advance");
        Git(fixture.Repository, true, "add", "later.txt");
        Git(fixture.Repository, true, "commit", "-m", "advance target while conflict waits");
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Conflict}, Checkpoint = {(int)WorktreeMergeCheckpoint.Rebase} WHERE Id = {request.Id}");

        var completed = await fixture.Queue.ResumeAsync(fixture.Slug, request.Id, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "later.txt")));
        Assert.True(ExistsAtTip(fixture.Repository, "later.txt"));
        var merged = ShowAtTip(fixture.Repository, relativePath);
        Assert.Contains("- ticket lesson", merged);
        Assert.Contains("- target lesson", merged);
        Assert.False(Directory.Exists(request.WorktreePath));
    }

    [Fact]
    public async Task MixedMemoryAndCodeConflicts_RemainEntirelyManual()
    {
        using var fixture = await Fixture.CreateAsync();
        var memory = Path.Combine(".agents", "processors", "column-12", "memory", "pipeline-lessons.md");
        var code = "shared.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Repository, memory))!);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, memory), "memory base\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, code), "code base\n");
        Git(fixture.Repository, true, "add", memory, code);
        Git(fixture.Repository, true, "commit", "-m", "mixed base");

        var ticket = await fixture.CreateTicketAsync();
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, ticket, CancellationToken.None))!;
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, memory), "ticket memory\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, code), "ticket code\n");
        Git(worktree.Path, true, "add", memory, code);
        Git(worktree.Path, true, "commit", "-m", "ticket mixed changes");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, memory), "target memory\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, code), "target code\n");
        Git(fixture.Repository, true, "add", memory, code);
        Git(fixture.Repository, true, "commit", "-m", "target mixed changes");

        var conflict = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Conflict, conflict!.Status);
        Assert.Contains(memory.Replace('\\', '/'), conflict.ConflictFiles);
        Assert.Contains(code, conflict.ConflictFiles);
        var unresolved = Git(request.WorktreePath, true, "diff", "--name-only", "--diff-filter=U").Output;
        Assert.Contains(memory.Replace('\\', '/'), unresolved);
        Assert.Contains(code, unresolved);
        Assert.True(Directory.Exists(request.WorktreePath));
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
        Assert.Equal("resolved merge\n", ShowAtTip(fixture.Repository, "shared.txt").Replace("\r\n", "\n"));
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
        Assert.Equal("durable lesson", ShowAtTip(fixture.Repository, "memory.md"));
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
        Assert.Equal("preserve me", ShowAtTip(fixture.Repository, relativeMemory));
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
        Assert.True(ExistsAtTip(fixture.Repository, "tracked.txt"));
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
        Assert.Equal("late commit", ShowAtTip(fixture.Repository, "maintenance.txt"));
    }

    [Fact]
    public async Task PrepareMaintenance_ConcurrentCallsReuseTheSameActiveRequest()
    {
        using var fixture = await Fixture.CreateAsync();
        var worktreePath = Path.Combine(fixture.Root.Path, "maintenance-worktree");
        Git(fixture.Repository, true, "worktree", "add", "-b", "maintenance/test", worktreePath, "integration");
        var queues = Enumerable.Range(0, 8)
            .Select(_ => new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees))
            .ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var preparations = queues.Select(async queue =>
        {
            await start.Task;
            return await queue.PrepareMaintenanceAsync(
                fixture.Slug, worktreePath, "maintenance/test", CancellationToken.None);
        }).ToArray();
        start.SetResult();

        var requests = await Task.WhenAll(preparations);

        Assert.Single(requests.Select(request => request.Id).Distinct());
        Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        Assert.All(requests, request => Assert.Equal(WorktreeMergeStatus.CommitPending, request.Status));
    }

    [Fact]
    public async Task PrepareMaintenance_ConcurrentCallsAfterQuarantineCreateASingleActiveRequest()
    {
        using var fixture = await Fixture.CreateAsync();
        var quarantinePath = Path.Combine(fixture.Root.Path, "maintenance-quarantine");
        Git(fixture.Repository, true, "worktree", "add", "-b", "recovery/maintenance-test", quarantinePath, "integration");
        await fixture.Queue.QuarantineMaintenanceAsync(
            fixture.Slug, Path.Combine(fixture.Root.Path, "maintenance-worktree"),
            quarantinePath, "recovery/maintenance-test",
            "Interrupted maintenance files require quarantine: transport.txt");
        var worktreePath = Path.Combine(fixture.Root.Path, "maintenance-worktree");
        Git(fixture.Repository, true, "worktree", "add", "-b", "maintenance/test", worktreePath, "integration");
        var queues = Enumerable.Range(0, 8)
            .Select(_ => new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees))
            .ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var preparations = queues.Select(async queue =>
        {
            await start.Task;
            return await queue.PrepareMaintenanceAsync(
                fixture.Slug, worktreePath, "maintenance/test", CancellationToken.None);
        }).ToArray();
        start.SetResult();

        var requests = await Task.WhenAll(preparations);

        Assert.Single(requests.Select(request => request.Id).Distinct());
        Assert.All(requests, request => Assert.Equal(WorktreeMergeStatus.CommitPending, request.Status));
        var rows = await fixture.Queue.ListAsync(fixture.Slug);
        Assert.Equal(2, rows.Count);
        Assert.Equal(WorktreeMergeStatus.Quarantined, rows[0].Status);
        Assert.Contains("require quarantine", rows[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareMaintenance_ReturnsTheActiveRequestShadowedByANewerQuarantineRow()
    {
        using var fixture = await Fixture.CreateAsync();
        var worktreePath = Path.Combine(fixture.Root.Path, "maintenance-worktree");
        Git(fixture.Repository, true, "worktree", "add", "-b", "maintenance/test", worktreePath, "integration");
        var active = await fixture.Queue.PrepareMaintenanceAsync(
            fixture.Slug, worktreePath, "maintenance/test", CancellationToken.None);
        var quarantinePath = Path.Combine(fixture.Root.Path, "maintenance-quarantine");
        Git(fixture.Repository, true, "worktree", "add", "-b", "recovery/maintenance-test", quarantinePath, "integration");
        await fixture.Queue.QuarantineMaintenanceAsync(
            fixture.Slug, Path.Combine(fixture.Root.Path, "unrelated-worktree"),
            quarantinePath, "recovery/maintenance-test",
            "Interrupted maintenance files require quarantine: transport.txt");

        var reused = await fixture.Queue.PrepareMaintenanceAsync(
            fixture.Slug, worktreePath, "maintenance/test", CancellationToken.None);

        Assert.Equal(active.Id, reused.Id);
        Assert.Equal(WorktreeMergeStatus.CommitPending, reused.Status);
        Assert.Equal(2, (await fixture.Queue.ListAsync(fixture.Slug)).Count);
    }

    [Fact]
    public async Task Recovery_RequeuesACommittedMaintenanceWorktreeAfterItsPreviousIntegrationCompleted()
    {
        using var fixture = await Fixture.CreateAsync();
        var worktreePath = Path.Combine(fixture.Root.Path, "maintenance-worktree");
        Git(fixture.Repository, true, "worktree", "add", "-b", "maintenance/test", worktreePath, "integration");
        var request = await fixture.Queue.PrepareMaintenanceAsync(
            fixture.Slug, worktreePath, "maintenance/test", CancellationToken.None);
        var baseline = Git(worktreePath, true, "rev-parse", "HEAD").Output.Trim();
        await fixture.Queue.MarkMaintenanceNoChangesAsync(fixture.Slug, request.Id, baseline);
        fixture.Queue.ReleaseMaintenanceWrite(request.Id);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "late.txt"), "preserved");
        Git(worktreePath, true, "add", "late.txt");
        Git(worktreePath, true, "commit", "-m", "late durable write");
        var lateHead = Git(worktreePath, true, "rev-parse", "HEAD").Output.Trim();

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var pending = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(1, recovered);
        Assert.Equal(WorktreeMergeStatus.Pending, pending.Status);
        Assert.Equal(lateHead, pending.SourceCommit);

        var completed = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal("preserved", ShowAtTip(fixture.Repository, "late.txt"));
    }

    [Fact]
    public async Task Recovery_LeavesAQuarantinedMaintenanceWorktreeUntouched()
    {
        using var fixture = await Fixture.CreateAsync();
        var quarantinePath = Path.Combine(fixture.Root.Path, "maintenance-quarantine");
        Git(fixture.Repository, true, "worktree", "add", "-b", "recovery/maintenance-test", quarantinePath, "integration");
        await File.WriteAllTextAsync(Path.Combine(quarantinePath, "transport.txt"), "local-only payload");
        await fixture.Queue.QuarantineMaintenanceAsync(
            fixture.Slug, Path.Combine(fixture.Root.Path, "maintenance-worktree"),
            quarantinePath, "recovery/maintenance-test",
            "Interrupted maintenance files require quarantine: transport.txt");

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var row = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(0, recovered);
        Assert.Equal(WorktreeMergeStatus.Quarantined, row.Status);
        Assert.Contains("require quarantine", row.Error, StringComparison.Ordinal);
        Assert.Equal("local-only payload",
            await File.ReadAllTextAsync(Path.Combine(quarantinePath, "transport.txt")));
    }

    [Fact]
    public async Task Resume_IntegratesAReviewedQuarantinedMaintenanceWorktree()
    {
        using var fixture = await Fixture.CreateAsync();
        var quarantinePath = Path.Combine(fixture.Root.Path, "maintenance-quarantine");
        Git(fixture.Repository, true, "worktree", "add", "-b", "recovery/maintenance-test", quarantinePath, "integration");
        await File.WriteAllTextAsync(Path.Combine(quarantinePath, "transport.txt"), "reviewed payload");
        Git(quarantinePath, true, "add", "transport.txt");
        Git(quarantinePath, true, "commit", "-m", "preserve reviewed maintenance payload");
        await fixture.Queue.QuarantineMaintenanceAsync(
            fixture.Slug, Path.Combine(fixture.Root.Path, "maintenance-worktree"),
            quarantinePath, "recovery/maintenance-test",
            "Interrupted maintenance files require quarantine: transport.txt");
        var quarantined = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        var completed = await fixture.Queue.ResumeAsync(fixture.Slug, quarantined.Id, CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
        Assert.Equal("reviewed payload", ShowAtTip(fixture.Repository, "transport.txt"));
        Assert.False(Directory.Exists(quarantinePath));
    }

    [Fact]
    public async Task Recovery_ClosesAReviewRowWhenItsCleanHeadIsAlreadyIntegrated()
    {
        using var fixture = await Fixture.CreateAsync();
        var worktreePath = Path.Combine(fixture.Root.Path, "maintenance-worktree");
        Git(fixture.Repository, true, "worktree", "add", "-b", "maintenance/test", worktreePath, "integration");
        var request = await fixture.Queue.PrepareMaintenanceAsync(
            fixture.Slug, worktreePath, "maintenance/test", CancellationToken.None);
        var head = Git(worktreePath, true, "rev-parse", "HEAD").Output.Trim();
        await fixture.Queue.MarkMaintenanceNoChangesAsync(fixture.Slug, request.Id, head);
        fixture.Queue.ReleaseMaintenanceWrite(request.Id);
        await fixture.Queue.MarkReviewRequiredAsync(fixture.Slug, request.Id, "simulated stale review");

        var recovered = await fixture.Queue.RecoverTerminalWorktreesAsync(fixture.Slug, CancellationToken.None);
        var completed = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(1, recovered);
        Assert.Equal(WorktreeMergeStatus.Completed, completed.Status);
        Assert.Equal(head, completed.IntegratedCommit);
        Assert.Null(completed.Error);
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
        Assert.True(ExistsAtTip(fixture.Repository, "candidate.txt"));
        Assert.True(ExistsAtTip(fixture.Repository, "concurrent.txt"));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "candidate.txt")));
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
        if (checkpoint == WorktreeMergeCheckpoint.Merge)
            Assert.Equal(0, Git(fixture.Repository, false, "merge-base", "--is-ancestor", result.IntegratedCommit!, "integration").ExitCode);
        else
            Assert.True(TipContains(fixture.Repository, result.IntegratedCommit!));
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

    [Fact]
    public async Task Integration_LeavesThePrimaryCheckoutFingerprintUntouched()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var before = CheckoutFingerprint(fixture.Repository);

        var result = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal(before, CheckoutFingerprint(fixture.Repository));
        Assert.Equal(TipCommit(fixture.Repository), result.IntegratedCommit);
        Assert.True(ExistsAtTip(fixture.Repository, "feature.txt"));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "feature.txt")));
    }

    [Fact]
    public async Task LocalSync_CleanCheckoutFastForwardsToIntegratedTip()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        var synced = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, synced!.SyncStatus);
        Assert.Equal(integrated!.IntegratedCommit, Git(fixture.Repository, true, "rev-parse", "HEAD").Output.Trim());
        Assert.Equal("feature", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "feature.txt")));
    }

    [Fact]
    public async Task LocalSync_RegistersACoordinatedMutationWindow()
    {
        using var fixture = await Fixture.CreateAsync();
        var registry = new PrimaryCheckoutActivityRegistry();
        var since = DateTime.UtcNow;
        var activeDuringSync = false;
        var queue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees,
            beforeLocalSyncCompletion: repository =>
                activeDuringSync = registry.HasCoordinatedMutationSince(repository, DateTime.UtcNow),
            primaryCheckoutActivity: registry);
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        // Integration itself never mutates the local checkout, so no window may be recorded yet.
        Assert.False(registry.HasCoordinatedMutationSince(fixture.Repository, since));

        var synced = await queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, synced!.SyncStatus);
        Assert.True(activeDuringSync);
        Assert.True(registry.HasCoordinatedMutationSince(fixture.Repository, since));
        Assert.False(registry.HasCoordinatedMutationSince(fixture.Repository, DateTime.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task LocalSync_NonConflictingTrackedStagedAndUntrackedWorkIsRestoredExactly()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "tracked.txt"), "base");
        Git(fixture.Repository, true, "add", "tracked.txt");
        Git(fixture.Repository, true, "commit", "-m", "tracked baseline");
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "tracked.txt"), "staged");
        Git(fixture.Repository, true, "add", "tracked.txt");
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository, "tracked.txt"), " and unstaged");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "local.txt"), "untracked");

        var synced = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, synced!.SyncStatus);
        Assert.Contains("MM tracked.txt", Git(fixture.Repository, true, "status", "--porcelain").Output);
        Assert.Contains("?? local.txt", Git(fixture.Repository, true, "status", "--porcelain").Output);
        Assert.Equal("staged and unstaged", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "tracked.txt")));
    }

    [Fact]
    public async Task LocalSync_ConflictKeepsRecoverableBackupAndDoesNotBlockLaterIntegration()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "base");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "shared baseline");
        var first = await fixture.CreateCommittedTicketAsync("shared.txt", "integrated");
        await fixture.Queue.EnqueueAsync(fixture.Slug, first, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "local");

        var conflict = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Conflict, conflict!.SyncStatus);
        Assert.Contains("shared.txt", conflict.SyncConflictFiles!);
        Assert.NotNull(conflict.SyncBackupRef);
        Assert.Equal(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", conflict.SyncBackupRef!).ExitCode);
        Git(fixture.Repository, true, "reset", "--merge");
        var second = await fixture.CreateCommittedTicketAsync("later.txt", "later");
        await fixture.Queue.EnqueueAsync(fixture.Slug, second, CancellationToken.None);
        var later = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed, later!.Status);
    }

    [Fact]
    public async Task LocalSync_ConflictDiscardedByHardResetRestoresBackupBeforeCompletion()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "base");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "shared baseline");
        var first = await fixture.CreateCommittedTicketAsync("shared.txt", "integrated");
        await fixture.Queue.EnqueueAsync(fixture.Slug, first, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "local");
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "note.txt"), "preserved");
        var conflict = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);
        Assert.Equal(LocalCheckoutSyncStatus.Conflict, conflict!.SyncStatus);
        Git(fixture.Repository, true, "reset", "--hard", conflict.SyncTargetCommit!);
        Git(fixture.Repository, true, "clean", "-fd");
        var second = await fixture.CreateCommittedTicketAsync("shared.txt", "local");
        await fixture.Queue.EnqueueAsync(fixture.Slug, second, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        var restoredBeforeCleanup = false;
        var retryQueue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees,
            afterSyncCompletionPersisted: repository => restoredBeforeCleanup =
                File.Exists(Path.Combine(repository, "note.txt"))
                && Git(repository, false, "show-ref", "--verify", "--quiet", conflict.SyncBackupRef!).ExitCode == 0);

        var retried = await retryQueue.RetrySynchronizationAsync(fixture.Slug, conflict.Id, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, retried!.SyncStatus);
        Assert.True(restoredBeforeCleanup);
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "note.txt")));
        Assert.Null(retried.SyncBackupRef);
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", conflict.SyncBackupRef!).ExitCode);
    }

    [Fact]
    public async Task LocalSync_ConflictResolvedInPlaceCompletesWithoutReapplyingBackup()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "base");
        Git(fixture.Repository, true, "add", "shared.txt");
        Git(fixture.Repository, true, "commit", "-m", "shared baseline");
        var ticket = await fixture.CreateCommittedTicketAsync("shared.txt", "integrated");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "local");
        var conflict = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);
        Assert.Equal(LocalCheckoutSyncStatus.Conflict, conflict!.SyncStatus);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "shared.txt"), "resolved in place");
        Git(fixture.Repository, true, "add", "shared.txt");

        var retried = await new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees)
            .RetrySynchronizationAsync(fixture.Slug, conflict.Id, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, retried!.SyncStatus);
        Assert.Equal("resolved in place", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "shared.txt")));
        Assert.Null(retried.SyncBackupRef);
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", conflict.SyncBackupRef!).ExitCode);
    }

    [Fact]
    public async Task LocalSync_DivergentLocalCommitIsPreservedAndActionable()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "local-commit.txt"), "local");
        Git(fixture.Repository, true, "add", "local-commit.txt");
        Git(fixture.Repository, true, "commit", "-m", "local divergent commit");
        var localHead = Git(fixture.Repository, true, "rev-parse", "HEAD").Output.Trim();

        var result = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Diverged, result!.SyncStatus);
        Assert.Equal(localHead, Git(fixture.Repository, true, "rev-parse", "HEAD").Output.Trim());
        Assert.Contains("Reconcile", result.SyncError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalSync_UntrackedCollisionKeepsBothCopiesRecoverable()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("collision.txt", "integrated");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "collision.txt"), "local untracked");

        var result = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Conflict, result!.SyncStatus);
        Assert.NotNull(result.SyncBackupRef);
        Assert.Equal("integrated", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "collision.txt")));
        Assert.Equal("local untracked", Git(fixture.Repository, true, "show", $"{result.SyncBackupRef}^3:collision.txt").Output);
    }

    [Fact]
    public async Task LocalSync_RestoresDurableBackupAfterCrashFollowingFastForward()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "local.txt"), "preserved");
        Git(fixture.Repository, true, "stash", "push", "--include-untracked", "--message", "simulated sync backup");
        var stash = Git(fixture.Repository, true, "rev-parse", "stash@{0}").Output.Trim();
        var backupRef = $"refs/kittyclaw/sync-backups/{integrated!.Id}";
        Git(fixture.Repository, true, "update-ref", backupRef, stash);
        Git(fixture.Repository, true, "stash", "drop", "stash@{0}");
        Git(fixture.Repository, true, "merge", "--ff-only", integrated.IntegratedCommit!);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)LocalCheckoutSyncStatus.Processing}, SyncBackupRef = {backupRef} WHERE Id = {integrated.Id}");

        var result = await new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees)
            .SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, result!.SyncStatus);
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "local.txt")));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", backupRef).ExitCode);
    }

    [Fact]
    public async Task LocalSync_RecoversProcessingCheckpointAndCatchesNewestIntegration()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateCommittedTicketAsync("first.txt", "first");
        await fixture.Queue.EnqueueAsync(fixture.Slug, first, CancellationToken.None);
        var row = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)LocalCheckoutSyncStatus.Processing} WHERE Id = {row!.Id}");
        var second = await fixture.CreateCommittedTicketAsync("second.txt", "second");
        await fixture.Queue.EnqueueAsync(fixture.Slug, second, CancellationToken.None);
        var newest = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        var recovered = await new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees)
            .SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, recovered!.SyncStatus);
        Assert.Equal(newest!.IntegratedCommit, Git(fixture.Repository, true, "rev-parse", "HEAD").Output.Trim());
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "second.txt")));
    }

    [Fact]
    public async Task LocalSync_ConcurrentWriteAfterBackupIsLeftUntouchedForRetry()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        var queue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees,
            beforeLocalSync: repository => File.WriteAllText(Path.Combine(repository, "concurrent.txt"), "external"));

        var result = await queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.ConcurrentChanges, result!.SyncStatus);
        Assert.Equal("external", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "concurrent.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, "feature.txt")));
    }

    [Fact]
    public async Task LocalSync_ConcurrentWriteAfterRestorePreservesBackupAndRemainsRetryable()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "local.txt"), "preserved");
        var queue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees,
            beforeLocalSyncCompletion: repository =>
                File.WriteAllText(Path.Combine(repository, "concurrent.txt"), "external"));

        var result = await queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.ConcurrentChanges, result!.SyncStatus);
        Assert.Equal("external", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "concurrent.txt")));
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "local.txt")));
        Assert.NotNull(result.SyncBackupRef);
        Assert.Equal(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", result.SyncBackupRef!).ExitCode);

        File.Delete(Path.Combine(fixture.Repository, "concurrent.txt"));
        Git(fixture.Repository, true, "reset", "--hard");
        Git(fixture.Repository, true, "clean", "-fd");
        var retried = await new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees)
            .RetrySynchronizationAsync(fixture.Slug, result.Id, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, retried!.SyncStatus);
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "local.txt")));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", result.SyncBackupRef!).ExitCode);
    }

    [Fact]
    public async Task LocalSync_ConcurrentCommitWithSameTreePreservesHeadAndBackupUntilRetry()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "local.txt"), "preserved");
        string? concurrentCommit = null;
        var queue = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees,
            beforeLocalSyncCompletion: repository =>
            {
                Git(repository, true, "commit", "--allow-empty", "-m", "concurrent local commit");
                concurrentCommit = Git(repository, true, "rev-parse", "HEAD").Output.Trim();
            });

        var result = await queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.ConcurrentChanges, result!.SyncStatus);
        Assert.Equal(concurrentCommit, Git(fixture.Repository, true, "rev-parse", "HEAD").Output.Trim());
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "local.txt")));
        Assert.NotNull(result.SyncBackupRef);
        Assert.Equal(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", result.SyncBackupRef!).ExitCode);

        Git(fixture.Repository, true, "reset", "--hard", result.SyncTargetCommit!);
        Git(fixture.Repository, true, "clean", "-fd");
        var retried = await new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees)
            .RetrySynchronizationAsync(fixture.Slug, result.Id, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, retried!.SyncStatus);
        Assert.Equal(result.SyncTargetCommit, Git(fixture.Repository, true, "rev-parse", "HEAD").Output.Trim());
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "local.txt")));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", result.SyncBackupRef!).ExitCode);
    }

    [Fact]
    public async Task LocalSync_RestartAfterDurableCompletionBeforeBackupCleanupFinishesIdempotently()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "local.txt"), "preserved");
        var interrupted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees,
            afterSyncCompletionPersisted: _ => throw new IOException("simulated interruption before backup cleanup"));

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.SynchronizeNextAsync(fixture.Slug, CancellationToken.None));

        var checkpoint = (await fixture.Queue.GetForTicketAsync(fixture.Slug, ticket)).Request;
        Assert.Equal(LocalCheckoutSyncStatus.CleanupPending, checkpoint!.SyncStatus);
        Assert.NotNull(checkpoint.SyncBackupRef);
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "local.txt")));
        Assert.Equal(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", checkpoint.SyncBackupRef!).ExitCode);

        var recovered = await new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees)
            .SynchronizeNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(LocalCheckoutSyncStatus.Completed, recovered!.SyncStatus);
        Assert.Null(recovered.SyncBackupRef);
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, "local.txt")));
        Assert.NotEqual(0, Git(fixture.Repository, false, "show-ref", "--verify", "--quiet", checkpoint.SyncBackupRef!).ExitCode);
        Assert.Null(await new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees)
            .SynchronizeNextAsync(fixture.Slug, CancellationToken.None));
    }

    [Fact]
    public async Task LocalSync_MissingCheckoutKeepsIntegrationSuccessful()
    {
        using var fixture = await Fixture.CreateAsync();
        var ticket = await fixture.CreateCommittedTicketAsync("feature.txt", "feature");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticket, CancellationToken.None);
        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        var moved = fixture.Repository + "-offline";
        Directory.Move(fixture.Repository, moved);
        try
        {
            var result = await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None);
            Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
            Assert.Equal(LocalCheckoutSyncStatus.CheckoutMissing, result!.SyncStatus);
            Assert.Contains("absent", result.SyncError, StringComparison.Ordinal);
        }
        finally { Directory.Move(moved, fixture.Repository); }
    }

    [Fact]
    public async Task CrashBetweenFastForwardAndTipPublication_IsResumedIdempotently()
    {
        using var fixture = await Fixture.CreateAsync();
        var firstTicket = await fixture.CreateCommittedTicketAsync("first.txt", "first");
        await fixture.Queue.EnqueueAsync(fixture.Slug, firstTicket, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
        var tipBeforeCrash = TipCommit(fixture.Repository);
        var secondTicket = await fixture.CreateCommittedTicketAsync("second.txt", "second");
        var request = await fixture.Queue.EnqueueAsync(fixture.Slug, secondTicket, CancellationToken.None);
        var worktree = (await fixture.Worktrees.ResolveAsync(fixture.Slug, secondTicket, CancellationToken.None))!;
        // Reproduce the crash window: the source is rebased and the integration worktree already
        // fast-forwarded, but the process stopped before the tip ref was published.
        Git(worktree.Path, true, "rebase", TipRef);
        var rebased = Git(worktree.Path, true, "rev-parse", "HEAD").Output.Trim();
        var integrationWorktree = Directory.EnumerateDirectories(
            Path.Combine(Path.GetDirectoryName(fixture.Repository)!, $"{Path.GetFileName(fixture.Repository)}.worktrees"),
            "integration-*").Single();
        Git(integrationWorktree, true, "merge", "--ff-only", rebased);
        await using (var db = fixture.Projects.GetProjectDb(fixture.Slug))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE WorktreeMergeQueue SET Status = 1, Checkpoint = {(int)WorktreeMergeCheckpoint.Merge} WHERE Id = {request.Id}");
        Assert.Equal(tipBeforeCrash, TipCommit(fixture.Repository));

        var restarted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);
        var result = await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, result!.Status);
        Assert.Equal(rebased, result.IntegratedCommit);
        Assert.Equal(rebased, TipCommit(fixture.Repository));
        Assert.False(Directory.Exists(request.WorktreePath));
        Assert.Null(await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentIntegrations_AcrossProjectsSharingOneRepository_AdvanceTheSharedTip()
    {
        using var fixture = await Fixture.CreateAsync();
        var projectB = await fixture.Projects.CreateProjectAsync("merge-queue-b");
        await fixture.Projects.UpdateProjectAsync(projectB.Slug, fixture.Workspace);
        await fixture.Projects.UpdateProjectAsync(projectB.Slug, null, worktreesEnabled: true, integrationBranch: "integration");
        var ticketsB = new TicketService(fixture.Projects, new MemberService(fixture.Projects));
        var worktreesB = new TicketWorktreeService(fixture.Projects, ticketsB);
        var queueB = new WorktreeMergeQueueService(fixture.Projects, worktreesB);
        // Offset project B ticket ids so the two projects use distinct worktree folders and branches.
        await ticketsB.CreateTicketAsync(projectB.Slug, "Offset ticket");
        var ticketA = await fixture.CreateCommittedTicketAsync("from-a.txt", "from project a");
        var ticketB = (await ticketsB.CreateTicketAsync(projectB.Slug, "Concurrent candidate")).Id;
        var worktreeB = (await worktreesB.ResolveAsync(projectB.Slug, ticketB, CancellationToken.None))!;
        await File.WriteAllTextAsync(Path.Combine(worktreeB.Path, "from-b.txt"), "from project b");
        Git(worktreeB.Path, true, "add", "from-b.txt");
        Git(worktreeB.Path, true, "commit", "-m", "from project b");
        await fixture.Queue.EnqueueAsync(fixture.Slug, ticketA, CancellationToken.None);
        await queueB.EnqueueAsync(projectB.Slug, ticketB, CancellationToken.None);
        var before = CheckoutFingerprint(fixture.Repository);

        var runA = Task.Run(() => fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None));
        var runB = Task.Run(() => queueB.ProcessNextAsync(projectB.Slug, CancellationToken.None));
        var results = await Task.WhenAll(runA, runB);

        Assert.All(results, result => Assert.Equal(WorktreeMergeStatus.Completed, result!.Status));
        Assert.True(ExistsAtTip(fixture.Repository, "from-a.txt"));
        Assert.True(ExistsAtTip(fixture.Repository, "from-b.txt"));
        Assert.Equal(before, CheckoutFingerprint(fixture.Repository));
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

    private const string TipRef = "refs/kittyclaw/integration/integration";

    private static string TipCommit(string repository) =>
        Git(repository, true, "rev-parse", TipRef).Output.Trim();

    private static bool TipContains(string repository, string commit) =>
        Git(repository, false, "merge-base", "--is-ancestor", commit, TipRef).ExitCode == 0;

    private static bool ExistsAtTip(string repository, string relativePath) =>
        Git(repository, false, "cat-file", "-e", $"{TipRef}:{relativePath.Replace('\\', '/')}").ExitCode == 0;

    private static string ShowAtTip(string repository, string relativePath) =>
        Git(repository, true, "show", $"{TipRef}:{relativePath.Replace('\\', '/')}").Output;

    private static (string Head, string TargetBranch, string Status) CheckoutFingerprint(string repository) => (
        Git(repository, true, "rev-parse", "HEAD").Output.Trim(),
        Git(repository, true, "rev-parse", "refs/heads/integration").Output.Trim(),
        Git(repository, true, "status", "--porcelain", "--untracked-files=all").Output);

    private static (int ExitCode, string Output, string Error) Git(string path, bool success, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (success) Assert.True(process.ExitCode == 0, error);
        return (process.ExitCode, output, error);
    }
}
