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
    public async Task MaintenanceWorktree_ExposesIgnoredLocalNodeDependencies_AndReusesThemAfterRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository, ".gitignore"), "\nnode_modules\n");
        Git(fixture.Repository, "add", ".gitignore");
        Git(fixture.Repository, "commit", "-m", "ignore local dependencies");
        var package = Path.Combine(fixture.Repository, ".agents", "tools", "node_modules", "local-package", "index.mjs");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        await File.WriteAllTextAsync(package, "export const available = true;\n");

        var first = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);
        var exposed = Path.Combine(first.RootPath, ".agents", "tools", "node_modules", "local-package", "index.mjs");

        Assert.True(File.Exists(exposed));
        Assert.Equal("export const available = true;\n", await File.ReadAllTextAsync(exposed));
        Assert.Empty(Git(first.RootPath, "status", "--porcelain", "--untracked-files=all"));
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, first, "chore: release dependency fixture");

        var restarted = new DurableWriteRouter(fixture.Projects, fixture.Worktrees);
        var second = await restarted.ResolveAsync(fixture.Slug, null, [".agents"]);

        Assert.Equal(first.RootPath, second.RootPath);
        Assert.True(File.Exists(exposed));
        Assert.Empty(Git(second.RootPath, "status", "--porcelain", "--untracked-files=all"));
        await restarted.CommitAndQueueAsync(fixture.Slug, second, "chore: release restarted dependency fixture");
    }

    [Fact]
    public async Task MaintenanceWorktree_ProductionTopology_DoesNotRediscoverWorktreeDependencies()
    {
        using var fixture = await Fixture.CreateAsync(productionTopology: true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, ".gitignore"), "node_modules\n");
        Git(fixture.Repository, "add", ".gitignore");
        Git(fixture.Repository, "commit", "-m", "ignore local dependencies");
        var package = Path.Combine(fixture.Workspace, ".agents", "tools", "node_modules", "local-package", "index.mjs");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        await File.WriteAllTextAsync(package, "export const available = true;\n");
        var unrelated = Path.Combine(fixture.Workspace, "Sources.worktrees", "ticket-other", "node_modules", "unrelated-package");
        Directory.CreateDirectory(unrelated);

        var first = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, first, "chore: release production topology fixture");
        var restarted = new DurableWriteRouter(fixture.Projects, fixture.Worktrees);
        var second = await restarted.ResolveAsync(fixture.Slug, null, [".agents"]);

        Assert.Equal(first.RootPath, second.RootPath);
        Assert.True(File.Exists(Path.Combine(second.RootPath, ".agents", "tools", "node_modules", "local-package", "index.mjs")));
        Assert.False(Directory.Exists(Path.Combine(second.RootPath, "Sources.worktrees")));
        Assert.False(Directory.Exists(Path.Combine(second.RootPath, "ticket-other", "node_modules")));
        Assert.Empty(Git(second.RootPath, "status", "--porcelain", "--untracked-files=all"));
        await restarted.CommitAndQueueAsync(fixture.Slug, second, "chore: release restarted production topology fixture");
    }

    [Fact]
    public async Task MaintenanceWorktree_WindowsMixedSeparators_ExposesOnlyCanonicalDependencies()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var fixture = await Fixture.CreateAsync(productionTopology: true, useForwardSlashesForWorkspace: true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, ".gitignore"), "node_modules\n");
        Git(fixture.Repository, "add", ".gitignore");
        Git(fixture.Repository, "commit", "-m", "ignore local dependencies");
        var package = Path.Combine(fixture.Workspace, ".agents", "tools", "node_modules", "local-package", "index.mjs");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        await File.WriteAllTextAsync(package, "export const available = true;\n");
        var unrelated = Path.Combine(fixture.Workspace, "Sources.worktrees", "ticket-other", "node_modules", "unrelated-package");
        Directory.CreateDirectory(unrelated);

        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);

        Assert.True(File.Exists(Path.Combine(route.RootPath, ".agents", "tools", "node_modules", "local-package", "index.mjs")));
        Assert.False(Directory.Exists(Path.Combine(route.RootPath, "Sources.worktrees")));
        Assert.False(Directory.Exists(Path.Combine(route.RootPath, "ticket-other", "node_modules")));
        Assert.Empty(Git(route.RootPath, "status", "--porcelain", "--untracked-files=all"));
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, route, "chore: release mixed separator fixture");
    }

    [Fact]
    public async Task MaintenanceWorktree_ReportsLocalDependencyPreparationFailureWithSourceAndTarget()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(fixture.Repository, ".gitignore"), "\nnode_modules\n");
        Git(fixture.Repository, "add", ".gitignore");
        Git(fixture.Repository, "commit", "-m", "ignore local dependencies");
        var source = Path.Combine(fixture.Repository, "node_modules");
        Directory.CreateDirectory(source);
        var initial = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]);
        await fixture.Router.CommitAndQueueAsync(fixture.Slug, initial, "chore: release dependency fixture");
        var target = Path.Combine(initial.RootPath, "node_modules");
        Directory.Delete(target);
        await File.WriteAllTextAsync(target, "collision");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Router.ResolveAsync(fixture.Slug, null, [".agents"]));

        Assert.Contains("Local dependency preparation failed", error.Message, StringComparison.Ordinal);
        Assert.Contains(target, error.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task CodeVariableNamedLikeASecret_IsAllowedAndStaged()
    {
        using var fixture = await Fixture.CreateAsync();
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, ["scripts"]);
        var directory = Path.Combine(route.RootPath, "scripts");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "reauth.mjs"),
            "const body = { client_secret: clientSecretV2, access_token: accessToken };\n");

        var result = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.Ready, result.Status);
        Assert.Empty(result.SecretPaths);
        Assert.Equal("scripts/reauth.mjs",
            Git(route.RootPath, "diff", "--cached", "--name-only").Trim().Replace('\\', '/'));
    }

    [Theory]
    [InlineData("client_secret: 'literalSecretValue'")]
    [InlineData("access_token=abcdefgh12345678")]
    public async Task LiteralAndProbableTokenValues_RemainBlocked(string content)
    {
        using var fixture = await Fixture.CreateAsync();
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, ["scripts"]);
        var directory = Path.Combine(route.RootPath, "scripts");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "reauth.mjs"), content);

        var result = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.SecretBlocked, result.Status);
        Assert.Single(result.SecretPaths);
        Assert.Empty(Git(route.RootPath, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task WholeWorkspaceRoute_StagesProjectChanges_ButRejectsLocalOnlyControlData()
    {
        using var fixture = await Fixture.CreateAsync();
        var route = await fixture.Router.TryResolveWorkspaceAsync(fixture.Slug);
        Assert.NotNull(route);
        await File.WriteAllTextAsync(Path.Combine(route!.RootPath, "new-project-file.txt"), "safe");

        var ready = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.Ready, ready.Status);
        Assert.Equal("new-project-file.txt",
            Git(route.RootPath, "diff", "--cached", "--name-only").Trim().Replace('\\', '/'));

        Git(route.RootPath, "reset", "HEAD", "--", "new-project-file.txt");
        var localOnly = Path.Combine(route.RootPath, ".agents", "channel", "tmp");
        Directory.CreateDirectory(localOnly);
        await File.WriteAllTextAsync(Path.Combine(localOnly, "transport.txt"), "ephemeral");

        var rejected = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.NeedsReview, rejected.Status);
        Assert.Contains(".agents/channel/tmp/transport.txt", rejected.UnexpectedPaths);
        Assert.Empty(Git(route.RootPath, "diff", "--cached", "--name-only"));
        await fixture.Router.PreserveExecutionAsync(fixture.Slug, route, "test cleanup");
    }

    [Fact]
    public async Task WholeWorkspaceRoute_HandlesRenamesWithoutLosingEitherPath()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "old-name.txt"), "tracked");
        Git(fixture.Repository, "add", "old-name.txt");
        Git(fixture.Repository, "commit", "-m", "add rename fixture");
        var route = await fixture.Router.TryResolveWorkspaceAsync(fixture.Slug);
        Assert.NotNull(route);
        File.Move(Path.Combine(route!.RootPath, "old-name.txt"), Path.Combine(route.RootPath, "new-name.txt"));

        var result = await fixture.Router.ValidateAndStageAsync(route);

        Assert.Equal(DurableWriteValidationStatus.Ready, result.Status);
        var staged = Git(route.RootPath, "diff", "--cached", "--name-status");
        Assert.Contains("old-name.txt", staged);
        Assert.Contains("new-name.txt", staged);
        await fixture.Router.PreserveExecutionAsync(fixture.Slug, route, "test cleanup");
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
        // Integration only advances the durable tip; the local checkout catches up on sync.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);
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
        // Integration only advances the durable tip; the local checkout catches up on sync.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await restarted.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);
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
        // Integration only advances the durable tip; the local checkout catches up on sync.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);
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
    public async Task UncommittedSafeMaintenanceWrite_IsCheckpointedAndIntegratedAfterRestart()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var route = await fixture.Router.ResolveAsync(fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(route.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Preserved after interruption\n");

        var restarted = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);
        var completed = await restarted.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        var request = Assert.Single(await restarted.ListAsync(fixture.Slug));
        Assert.Equal(WorktreeMergeStatus.Completed, completed!.Status);
        Assert.Equal(WorktreeMergeStatus.Completed, request.Status);
        // Integration only advances the durable tip; the local checkout catches up on sync.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await restarted.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);
        Assert.Contains("Preserved after interruption",
            await File.ReadAllTextAsync(Path.Combine(
                fixture.Repository, ".agents", "programmer", "memory", "MEMORY.md")));
        Assert.Empty(Git(route.RootPath, "status", "--porcelain"));
        var restartedAgain = new WorktreeMergeQueueService(fixture.Projects, fixture.Worktrees);
        Assert.Null(await restartedAgain.ProcessNextAsync(fixture.Slug, CancellationToken.None));
        Assert.Equal(WorktreeMergeStatus.Completed,
            Assert.Single(await restartedAgain.ListAsync(fixture.Slug)).Status);
        fixture.Queue!.ReleaseMaintenanceWrite(route.QueueRequestId!.Value);
    }

    [Fact]
    public async Task NextInteractiveWrite_RecoversSafeUncommittedMaintenanceChanges()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var interrupted = await fixture.Router.ResolveAsync(
            fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(
            interrupted.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Preserved after interruption\n");
        await fixture.Router.PreserveExecutionAsync(
            fixture.Slug, interrupted, "preserved uncommitted changes");

        var recovered = await fixture.Router.TryResolveWorkspaceAsync(fixture.Slug);
        Assert.NotNull(recovered);
        var validation = await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, recovered!, "chore(chat): recover interrupted durable write");
        var queued = Assert.Single(await fixture.Queue!.ListAsync(fixture.Slug));

        Assert.Equal(DurableWriteValidationStatus.Ready, validation.Status);
        Assert.Equal(WorktreeMergeStatus.Pending, queued.Status);
        Assert.Empty(Git(recovered!.RootPath, "status", "--porcelain"));

        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        // Integration only advances the durable tip; the local checkout catches up on sync.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);
        Assert.Contains("Preserved after interruption",
            await File.ReadAllTextAsync(Path.Combine(
                fixture.Repository, ".agents", "programmer", "memory", "MEMORY.md")));
    }

    [Fact]
    public async Task NextInteractiveWrite_RequeuesCleanDivergentMaintenanceHistory()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var initial = await fixture.Router.ResolveAsync(
            fixture.Slug, null, [".agents/programmer/memory"]);
        var memory = Path.Combine(
            initial.RootPath, ".agents", "programmer", "memory", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        await File.WriteAllTextAsync(memory, "# Initial memory\n");
        await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, initial, "chore(memory): initial");
        Assert.Equal(WorktreeMergeStatus.Completed,
            (await fixture.Queue!.ProcessNextAsync(fixture.Slug, CancellationToken.None))!.Status);
        // Catch the local checkout up to the first integration before committing the external
        // change, so `main-only` sits on top of the integrated tip instead of diverging from it.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);

        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "main-only.txt"), "main\n");
        Git(fixture.Repository, "add", "main-only.txt");
        Git(fixture.Repository, "commit", "-m", "main change");
        await File.AppendAllTextAsync(memory, "Maintenance lesson.\n");
        Git(initial.RootPath, "add", ".agents/programmer/memory/MEMORY.md");
        Git(initial.RootPath, "commit", "-m", "maintenance change");

        var recovered = await fixture.Router.TryResolveWorkspaceAsync(fixture.Slug);
        Assert.NotNull(recovered);
        var validation = await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, recovered!, "chore(chat): recover divergent maintenance history");
        var queued = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));

        Assert.Equal(DurableWriteValidationStatus.Ready, validation.Status);
        Assert.Equal(WorktreeMergeStatus.Pending, queued.Status);

        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);
        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        // Integration only advances the durable tip; the local checkout catches up on sync.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);
        Assert.True(File.Exists(Path.Combine(fixture.Repository, "main-only.txt")));
        Assert.Contains("Maintenance lesson",
            await File.ReadAllTextAsync(Path.Combine(
                fixture.Repository, ".agents", "programmer", "memory", "MEMORY.md")));
    }

    [Fact]
    public async Task NextInteractiveWrite_QuarantinesLocalOnlyInterruptedChangesAndContinues()
    {
        using var fixture = await Fixture.CreateAsync(withQueue: true);
        var interrupted = await fixture.Router.TryResolveWorkspaceAsync(fixture.Slug);
        Assert.NotNull(interrupted);
        var localOnly = Path.Combine(
            interrupted!.RootPath, ".agents", "channel", "tmp", "transport.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(localOnly)!);
        await File.WriteAllTextAsync(localOnly, "ephemeral\n");
        await fixture.Router.PreserveExecutionAsync(
            fixture.Slug, interrupted, "preserved local-only changes");

        var resumed = await fixture.Router.TryResolveWorkspaceAsync(fixture.Slug);
        Assert.NotNull(resumed);
        Assert.Equal(interrupted.RootPath, resumed!.RootPath);
        Assert.False(File.Exists(Path.Combine(
            resumed.RootPath, ".agents", "channel", "tmp", "transport.txt")));

        var rows = await fixture.Queue!.ListAsync(fixture.Slug);
        var quarantined = Assert.Single(rows,
            row => row.Status == WorktreeMergeStatus.Quarantined);
        var replacement = Assert.Single(rows,
            row => row.Status == WorktreeMergeStatus.CommitPending);
        Assert.NotEqual(quarantined.Id, replacement.Id);
        Assert.Contains("changes that require review", quarantined.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            quarantined.WorktreePath, ".agents", "channel", "tmp", "transport.txt")));

        await fixture.Router.CommitAndQueueAsync(
            fixture.Slug, resumed, "chore(chat): continue after quarantine");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _root;
        public string Repository { get; }
        public string Workspace { get; }
        public string Slug { get; }
        public TicketService Tickets { get; }
        public ProjectService Projects { get; }
        public TicketWorktreeService Worktrees { get; }
        public DurableWriteRouter Router { get; }
        public WorktreeMergeQueueService? Queue { get; }

        private Fixture(TempDir root, string workspace, string repository, string slug, ProjectService projects,
            TicketService tickets, TicketWorktreeService worktrees, DurableWriteRouter router, WorktreeMergeQueueService? queue)
            => (_root, Workspace, Repository, Slug, Projects, Tickets, Worktrees, Router, Queue) =
                (root, workspace, repository, slug, projects, tickets, worktrees, router, queue);

        public static async Task<Fixture> CreateAsync(
            bool withQueue = false,
            bool productionTopology = false,
            bool useForwardSlashesForWorkspace = false)
        {
            var root = new TempDir();
            var workspace = root.Path;
            var repository = productionTopology
                ? Path.Combine(workspace, "Sources")
                : ProjectWorktreeSettingsTests.CreateRepository(workspace, "integration");
            if (productionTopology)
            {
                Directory.CreateDirectory(repository);
                Git(repository, "init", "--initial-branch", "integration");
                Git(repository, "config", "user.email", "tests@kittyclaw.local");
                Git(repository, "config", "user.name", "KittyClaw Tests");
                Git(repository, "commit", "--allow-empty", "-m", "initial");
            }
            var projects = new ProjectService(Path.Combine(root.Path, "data"));
            var project = await projects.CreateProjectAsync("router");
            var configuredWorkspace = productionTopology ? workspace : repository;
            if (useForwardSlashesForWorkspace)
                configuredWorkspace = configuredWorkspace.Replace('\\', '/');
            await projects.UpdateProjectAsync(project.Slug, configuredWorkspace);
            await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true, integrationBranch: "integration",
                repositoryPath: productionTopology ? repository : null);
            var tickets = new TicketService(projects, new MemberService(projects));
            var worktrees = new TicketWorktreeService(projects, tickets);
            var queue = withQueue ? new WorktreeMergeQueueService(projects, worktrees) : null;
            return new Fixture(root, workspace, repository, project.Slug, projects, tickets, worktrees,
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
