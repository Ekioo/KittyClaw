using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Data;

namespace KittyClaw.Core.Services;

public enum WorktreeMergeStatus
{
    Pending = 0,
    Processing = 1,
    Conflict = 2,
    Failed = 3,
    Completed = 4,
    CommitPending = 5,
    ValidationRequired = 6,
    NeedsReview = 7,
    BlockedByExternalChanges = 8,
    Quarantined = 9
}

public enum WorktreeMergeJobKind { Ticket = 0, Maintenance = 1 }
public enum WorktreeMergeCheckpoint { Preparation = 0, Writing = 1, Validation = 2, Commit = 3, Waiting = 4, Rebase = 5, Merge = 6 }
public enum LocalCheckoutSyncStatus
{
    NotRequired = 0, Pending = 1, Processing = 2, Completed = 3, Conflict = 4,
    Diverged = 5, CheckoutMissing = 6, ConcurrentChanges = 7, CleanupPending = 8
}

public sealed record WorktreeMergeRequest(
    long Id, int TicketId, int RootTicketId, string WorktreePath, string SourceBranch,
    string TargetBranch, WorktreeMergeStatus Status, DateTime CreatedAt, DateTime UpdatedAt,
    string? SourceCommit, string? IntegratedCommit, string? Error, string? ConflictFiles,
    WorktreeMergeJobKind JobKind = WorktreeMergeJobKind.Ticket,
    WorktreeMergeCheckpoint Checkpoint = WorktreeMergeCheckpoint.Preparation,
    DateTime? LocalIntegratedAt = null, DateTime? RemotePublishedAt = null,
    LocalCheckoutSyncStatus SyncStatus = LocalCheckoutSyncStatus.NotRequired,
    string? SyncTargetCommit = null, string? SyncBackupRef = null,
    string? SyncError = null, string? SyncConflictFiles = null, DateTime? SyncUpdatedAt = null)
{
    public bool HasSynchronizationLag => Status == WorktreeMergeStatus.Completed
        && SyncStatus != LocalCheckoutSyncStatus.Completed;
}

public sealed record WorktreeMergeAlertSummary(
    int ActiveCount, WorktreeMergeStatus MostSevereStatus, DateTime OldestUpdatedAt);

/// <summary>
/// Durable, per-project serialized integration queue for canonical ticket worktrees.
/// The integration phase advances a per-target integration tip (<c>refs/kittyclaw/integration/&lt;target&gt;</c>)
/// through a dedicated detached-HEAD integration worktree and never mutates the primary checkout;
/// advancing the local checkout to the tip is a separate synchronization concern.
/// </summary>
public sealed partial class WorktreeMergeQueueService(
    ProjectService projects,
    TicketWorktreeService worktrees,
    WorktreeFinalizationCoordinator? finalization = null,
    AgentRunRegistry? runs = null,
    Action<string, string>? beforeFastForward = null,
    Action<string>? beforeLocalSync = null,
    Action<string>? beforeLocalSyncCompletion = null,
    Action<string>? afterSyncCompletionPersisted = null,
    PrimaryCheckoutActivityRegistry? primaryCheckoutActivity = null)
{
    private const int MaxTargetAdvanceRetries = 3;
    private const string DivergedTipError =
        "The target branch received external commits that are not part of the integration tip. "
        + "Reconcile the target branch with the integration tip (for example merge the integration tip ref "
        + "into the target branch), then resume; integration is paused until the divergence is resolved.";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, byte> _activeMaintenanceWrites = new();

    private static async Task EnsureTableAsync(TodoDbContext db)
    {
        await MigrationGate.RunOnceAsync(db, "worktree-merge-queue-v1", static d =>
            d.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS WorktreeMergeQueue (
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
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorktreeMergeQueue_ActiveRoot
                ON WorktreeMergeQueue(RootTicketId) WHERE Status IN (0, 1, 2, 3);
            CREATE INDEX IF NOT EXISTS IX_WorktreeMergeQueue_StatusId ON WorktreeMergeQueue(Status, Id);
            """));
        await MigrationGate.RunOnceAsync(db, "worktree-merge-queue-v2-active-states", static d =>
            d.Database.ExecuteSqlRawAsync("""
                DROP INDEX IF EXISTS IX_WorktreeMergeQueue_ActiveRoot;
                UPDATE WorktreeMergeQueue AS candidate
                SET Status = 4,
                    UpdatedAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    Error = CASE
                        WHEN Error IS NULL OR Error = '' THEN 'Superseded duplicate integration preserved during queue migration.'
                        ELSE Error || char(10) || 'Superseded duplicate integration preserved during queue migration.'
                    END
                WHERE Status <> 4
                  AND EXISTS (
                      SELECT 1
                      FROM WorktreeMergeQueue AS newer
                      WHERE newer.RootTicketId = candidate.RootTicketId
                        AND newer.Status <> 4
                        AND newer.Id > candidate.Id
                  );
                CREATE UNIQUE INDEX IX_WorktreeMergeQueue_ActiveRoot
                    ON WorktreeMergeQueue(RootTicketId) WHERE Status <> 4;
                """));
        await MigrationGate.RunOnceAsync(db, "worktree-merge-queue-v3-checkpoints", static async d =>
        {
            foreach (var sql in new[]
            {
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN JobKind INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN Checkpoint INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN LocalIntegratedAt TEXT NULL",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN RemotePublishedAt TEXT NULL"
            })
            {
                try { await d.Database.ExecuteSqlRawAsync(sql); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
            }
        });
        await MigrationGate.RunOnceAsync(db, "worktree-merge-queue-v4-quarantine", static d =>
            d.Database.ExecuteSqlRawAsync($"""
                DROP INDEX IF EXISTS IX_WorktreeMergeQueue_ActiveRoot;
                UPDATE WorktreeMergeQueue AS candidate
                SET Status = {(int)WorktreeMergeStatus.Completed},
                    UpdatedAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    Error = CASE
                        WHEN Error IS NULL OR Error = '' THEN 'Superseded duplicate integration preserved during queue migration.'
                        ELSE Error || char(10) || 'Superseded duplicate integration preserved during queue migration.'
                    END
                WHERE Status NOT IN ({(int)WorktreeMergeStatus.Completed}, {(int)WorktreeMergeStatus.Quarantined})
                  AND EXISTS (
                      SELECT 1
                      FROM WorktreeMergeQueue AS newer
                      WHERE newer.RootTicketId = candidate.RootTicketId
                        AND newer.Status NOT IN ({(int)WorktreeMergeStatus.Completed}, {(int)WorktreeMergeStatus.Quarantined})
                        AND newer.Id > candidate.Id
                  );
                CREATE UNIQUE INDEX IX_WorktreeMergeQueue_ActiveRoot
                    ON WorktreeMergeQueue(RootTicketId)
                    WHERE Status NOT IN ({(int)WorktreeMergeStatus.Completed}, {(int)WorktreeMergeStatus.Quarantined});
                """));
        await MigrationGate.RunOnceAsync(db, "worktree-merge-queue-v5-local-sync", static async d =>
        {
            foreach (var sql in new[]
            {
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN SyncStatus INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN SyncTargetCommit TEXT NULL",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN SyncBackupRef TEXT NULL",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN SyncError TEXT NULL",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN SyncConflictFiles TEXT NULL",
                "ALTER TABLE WorktreeMergeQueue ADD COLUMN SyncUpdatedAt TEXT NULL"
            })
            {
                try { await d.Database.ExecuteSqlRawAsync(sql); }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
            }
        });
    }

    public async Task<WorktreeMergeRequest> EnqueueAsync(string projectSlug, int ticketId, CancellationToken ct)
        => await EnqueueAsync(projectSlug, ticketId, allowDisabledProject: false, ct, registeredWorktree: null);

    private async Task<WorktreeMergeRequest> EnqueueAsync(
        string projectSlug, int ticketId, bool allowDisabledProject, CancellationToken ct,
        TicketWorktree? registeredWorktree)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        if ((!project.WorktreesEnabled && !allowDisabledProject) || string.IsNullOrWhiteSpace(project.IntegrationBranch))
            throw new InvalidOperationException("Worktree integration is not enabled for this project.");
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var rootTicketId = await worktrees.ResolveRootTicketIdAsync(projectSlug, ticketId);
        var existing = await ReadByRootAsync(db, rootTicketId);
        TicketWorktreeState? inspected = registeredWorktree is null
            ? null
            : InspectRegisteredWorktree(registeredWorktree, rootTicketId);
        if (existing is not null)
        {
            if (existing.Status != WorktreeMergeStatus.Completed)
                return existing;

            inspected ??= await worktrees.InspectAsync(projectSlug, ticketId);
            if (inspected is not { Exists: true })
                return existing;

            var head = RunGit(inspected.Path, ["rev-parse", "HEAD"], false);
            var repository = projects.ResolveRepositoryPath(project);
            if (!inspected.IsDirty
                && string.IsNullOrWhiteSpace(inspected.Error)
                && head.ExitCode == 0
                && IsIntegratedCommit(repository, head.Output.Trim(), project.IntegrationBranch))
            {
                var recoveryAt = DateTime.UtcNow;
                var sourceCommit = head.Output.Trim();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE WorktreeMergeQueue SET
                        TicketId = {ticketId}, WorktreePath = {inspected.Path},
                        SourceBranch = {inspected.Branch}, TargetBranch = {project.IntegrationBranch},
                        Status = {(int)WorktreeMergeStatus.Pending},
                        Checkpoint = {(int)WorktreeMergeCheckpoint.Waiting},
                        SourceCommit = {sourceCommit}, IntegratedCommit = NULL,
                        Error = 'Recovered registered worktree left after completed integration',
                        ConflictFiles = NULL, LocalIntegratedAt = NULL, RemotePublishedAt = NULL,
                        UpdatedAt = {recoveryAt}
                    WHERE Id = {existing.Id}
                    """);
                return (await ReadByIdAsync(db, existing.Id))!;
            }
        }
        TicketWorktree? worktree;
        if (registeredWorktree is not null && inspected is { Exists: true })
        {
            worktree = registeredWorktree with { RootTicketId = rootTicketId };
        }
        else if (inspected is { Exists: true })
        {
            worktree = new TicketWorktree(inspected.Path, inspected.Branch, inspected.RootTicketId,
                projects.ResolveRepositoryPath(project));
        }
        else if (allowDisabledProject)
        {
            var registered = await worktrees.InspectAsync(projectSlug, ticketId);
            worktree = registered is { Exists: true }
                ? new TicketWorktree(registered.Path, registered.Branch, registered.RootTicketId,
                    projects.ResolveRepositoryPath(project))
                : null;
        }
        else
        {
            worktree = await worktrees.ResolveAsync(projectSlug, ticketId, ct);
        }
        if (worktree is null)
            throw new InvalidOperationException("The ticket has no canonical worktree.");
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorktreeMergeQueue
                (TicketId, RootTicketId, WorktreePath, SourceBranch, TargetBranch, Status, CreatedAt, UpdatedAt, JobKind, Checkpoint)
            VALUES ({ticketId}, {worktree.RootTicketId}, {worktree.Path}, {worktree.Branch},
                {project.IntegrationBranch}, {(int)WorktreeMergeStatus.Pending}, {now}, {now},
                {(int)WorktreeMergeJobKind.Ticket}, {(int)WorktreeMergeCheckpoint.Waiting})
            """);
        return (await ReadByRootAsync(db, worktree.RootTicketId))!;
    }

    /// <summary>
    /// Discovers registered worktrees left behind for ticket families that are already terminal.
    /// Inspection is deliberately non-creating: recovery must never manufacture a worktree for
    /// an old completed ticket merely because its queue row is absent.
    /// </summary>
    public async Task<int> RecoverTerminalWorktreesAsync(string projectSlug, CancellationToken ct)
    {
        var project = await projects.GetProjectAsync(projectSlug);
        if (project is null || string.IsNullOrWhiteSpace(project.IntegrationBranch))
            return 0;

        var recovered = await RecoverMaintenanceWorktreeAsync(projectSlug, project, ct);
        var terminalRoots = (await worktrees.ListTerminalRootTicketsAsync(projectSlug))
            .ToHashSet();
        foreach (var registered in await worktrees.ListRegisteredAsync(projectSlug))
        {
            ct.ThrowIfCancellationRequested();
            var ticketId = registered.RootTicketId;
            int rootTicketId;
            try { rootTicketId = await worktrees.ResolveRootTicketIdAsync(projectSlug, ticketId); }
            catch (InvalidOperationException) { continue; }
            if (!terminalRoots.Contains(rootTicketId) || await IsBusyAsync(projectSlug, rootTicketId))
                continue;
            await EnqueueAsync(projectSlug, ticketId, allowDisabledProject: true, ct,
                registered with { RootTicketId = rootTicketId });
            recovered++;
        }
        return recovered;
    }

    private async Task<int> RecoverMaintenanceWorktreeAsync(
        string projectSlug, Project project, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var request = await ReadByRootAsync(db, int.MinValue);
        if (request is not { JobKind: WorktreeMergeJobKind.Maintenance })
            return 0;
        // A quarantined row deliberately keeps its dirty worktree for human review; recovering it
        // here would demote the quarantine to a generic review state and lose its reason.
        if (request.Status == WorktreeMergeStatus.Quarantined)
            return 0;
        if (_activeMaintenanceWrites.ContainsKey(request.Id))
            return 0;
        if (!Directory.Exists(request.WorktreePath))
            return 0;

        var branch = RunGit(request.WorktreePath, ["branch", "--show-current"], false);
        var head = RunGit(request.WorktreePath, ["rev-parse", "HEAD"], false);
        if (branch.ExitCode != 0 || head.ExitCode != 0 ||
            !string.Equals(branch.Output.Trim(), request.SourceBranch, StringComparison.Ordinal))
        {
            await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                "The maintenance worktree could not be verified during recovery.", null);
            return 1;
        }

        var changed = RunGit(request.WorktreePath,
            ["status", "--porcelain", "--untracked-files=all"], false);
        if (changed.ExitCode != 0 || !string.IsNullOrWhiteSpace(changed.Output))
        {
            await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                "The maintenance worktree contains uncommitted durable changes and requires recovery.", null);
            return 1;
        }

        var sourceCommit = head.Output.Trim();
        var repository = projects.ResolveRepositoryPath(project);
        if (IsIntegratedCommit(repository, sourceCommit, project.IntegrationBranch!))
        {
            if (request.Status == WorktreeMergeStatus.Completed &&
                string.Equals(request.IntegratedCommit, sourceCommit, StringComparison.Ordinal))
                return 0;
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Completed},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Merge}, SourceCommit = {sourceCommit},
                    IntegratedCommit = {sourceCommit}, Error = NULL, ConflictFiles = NULL,
                    LocalIntegratedAt = COALESCE(LocalIntegratedAt, {now}), UpdatedAt = {now}
                WHERE Id = {request.Id} AND TicketId = 0
                """);
            return 1;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Pending},
                Checkpoint = {(int)WorktreeMergeCheckpoint.Waiting}, SourceCommit = {sourceCommit},
                IntegratedCommit = NULL,
                Error = 'Recovered committed maintenance work after the previous integration completed',
                ConflictFiles = NULL, LocalIntegratedAt = NULL, RemotePublishedAt = NULL,
                UpdatedAt = {DateTime.UtcNow}
            WHERE Id = {request.Id} AND TicketId = 0
            """);
        return 1;
    }

    public async Task<WorktreeMergeRequest> PrepareMaintenanceAsync(
        string projectSlug, string worktreePath, string sourceBranch, CancellationToken ct)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await PrepareMaintenanceCoreAsync(projectSlug, worktreePath, sourceBranch, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorktreeMergeRequest> PrepareMaintenanceCoreAsync(
        string projectSlug, string worktreePath, string sourceBranch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        if (!project.WorktreesEnabled || string.IsNullOrWhiteSpace(project.IntegrationBranch))
            throw new InvalidOperationException("Worktree integration is not enabled for this project.");
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        const int maintenanceRoot = int.MinValue;
        // A quarantine insert creates a newer row for the same root; reading only the latest row
        // would shadow the single active request and make the insert below violate the unique index.
        var existing = await ReadActiveMaintenanceAsync(db) ?? await ReadByRootAsync(db, maintenanceRoot);
        if (existing is not null && existing.Status != WorktreeMergeStatus.Quarantined)
        {
            if (existing.Status == WorktreeMergeStatus.Completed)
            {
                var baseline = RunGit(worktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.CommitPending},
                        Checkpoint = {(int)WorktreeMergeCheckpoint.Writing}, SourceCommit = {baseline},
                        IntegratedCommit = NULL, Error = NULL, ConflictFiles = NULL,
                        LocalIntegratedAt = NULL, RemotePublishedAt = NULL, UpdatedAt = {DateTime.UtcNow}
                    WHERE Id = {existing.Id}
                    """);
                existing = (await ReadByIdAsync(db, existing.Id))!;
            }
            _activeMaintenanceWrites.TryAdd(existing.Id, 0);
            return existing;
        }
        var now = DateTime.UtcNow;
        var baselineCommit = RunGit(worktreePath, ["rev-parse", "HEAD"]).Output.Trim();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO WorktreeMergeQueue
                    (TicketId, RootTicketId, WorktreePath, SourceBranch, TargetBranch, Status, CreatedAt, UpdatedAt, SourceCommit, JobKind, Checkpoint)
                VALUES ({0}, {maintenanceRoot}, {worktreePath}, {sourceBranch}, {project.IntegrationBranch},
                    {(int)WorktreeMergeStatus.CommitPending}, {now}, {now}, {baselineCommit},
                    {(int)WorktreeMergeJobKind.Maintenance}, {(int)WorktreeMergeCheckpoint.Writing})
                """);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // A preparation outside this process gate won the insert; adopt its active request.
            var concurrent = await ReadActiveMaintenanceAsync(db);
            if (concurrent is not { JobKind: WorktreeMergeJobKind.Maintenance })
                throw;
            _activeMaintenanceWrites.TryAdd(concurrent.Id, 0);
            return concurrent;
        }
        var created = (await ReadActiveMaintenanceAsync(db))!;
        _activeMaintenanceWrites.TryAdd(created.Id, 0);
        return created;
    }

    public async Task QuarantineMaintenanceAsync(
        string projectSlug,
        string originalPath,
        string quarantinePath,
        string quarantineBranch,
        string reason)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        const int maintenanceRoot = int.MinValue;
        var existing = await ReadByRootAsync(db, maintenanceRoot);
        var now = DateTime.UtcNow;
        if (existing is { JobKind: WorktreeMergeJobKind.Maintenance }
            && string.Equals(existing.WorktreePath, originalPath, StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET WorktreePath = {quarantinePath},
                    SourceBranch = {quarantineBranch}, Status = {(int)WorktreeMergeStatus.Quarantined},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Validation}, Error = {reason},
                    ConflictFiles = NULL, UpdatedAt = {now}
                WHERE Id = {existing.Id}
                """);
            _activeMaintenanceWrites.TryRemove(existing.Id, out _);
            return;
        }

        var head = RunGit(quarantinePath, ["rev-parse", "HEAD"], false).Output.Trim();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorktreeMergeQueue
                (TicketId, RootTicketId, WorktreePath, SourceBranch, TargetBranch, Status,
                 CreatedAt, UpdatedAt, SourceCommit, JobKind, Checkpoint, Error)
            VALUES ({0}, {maintenanceRoot}, {quarantinePath}, {quarantineBranch},
                {project.IntegrationBranch!}, {(int)WorktreeMergeStatus.Quarantined},
                {now}, {now}, {head}, {(int)WorktreeMergeJobKind.Maintenance},
                {(int)WorktreeMergeCheckpoint.Validation}, {reason})
            """);
    }

    public async Task MarkMaintenanceReadyAsync(string projectSlug, long requestId, string sourceCommit)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Pending},
                Checkpoint = {(int)WorktreeMergeCheckpoint.Waiting}, SourceCommit = {sourceCommit}, Error = NULL, UpdatedAt = {DateTime.UtcNow}
            WHERE Id = {requestId} AND TicketId = 0
            """);
    }

    public async Task MarkMaintenanceNoChangesAsync(string projectSlug, long requestId, string sourceCommit)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var request = await ReadByIdAsync(db, requestId);
        if (request is null || request.JobKind != WorktreeMergeJobKind.Maintenance)
            return;
        if (request.Status != WorktreeMergeStatus.CommitPending)
            return;
        if (!string.Equals(request.SourceCommit, sourceCommit, StringComparison.Ordinal))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Pending},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Waiting}, SourceCommit = {sourceCommit},
                    Error = 'Recovered committed maintenance write while finalizing a no-op',
                    UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {requestId} AND TicketId = 0
                """);
            return;
        }

        var repository = projects.ResolveRepositoryPath(project);
        if (string.IsNullOrWhiteSpace(project.IntegrationBranch)
            || !IsIntegratedCommit(repository, sourceCommit, project.IntegrationBranch))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Pending},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Waiting}, SourceCommit = {sourceCommit},
                    IntegratedCommit = NULL,
                    Error = 'Recovered unintegrated maintenance commit while finalizing a no-op',
                    LocalIntegratedAt = NULL, UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {requestId} AND TicketId = 0
                """);
            return;
        }

        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Completed},
                Checkpoint = {(int)WorktreeMergeCheckpoint.Merge}, SourceCommit = {sourceCommit},
                IntegratedCommit = {sourceCommit}, Error = NULL, ConflictFiles = NULL,
                LocalIntegratedAt = {now}, UpdatedAt = {now}
            WHERE Id = {requestId} AND TicketId = 0
            """);
    }

    public void ReleaseMaintenanceWrite(long requestId) => _activeMaintenanceWrites.TryRemove(requestId, out _);

    public async Task MarkReviewRequiredAsync(string projectSlug, long requestId, string error)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        await MarkAsync(db, requestId, WorktreeMergeStatus.NeedsReview, error, null);
    }

    public async Task MarkValidationRequiredAsync(string projectSlug, long requestId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        await SetStateAsync(db, requestId, WorktreeMergeStatus.ValidationRequired, WorktreeMergeCheckpoint.Validation);
    }

    public async Task MarkPublishedAsync(string projectSlug, long requestId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE WorktreeMergeQueue SET RemotePublishedAt = {DateTime.UtcNow}, UpdatedAt = {DateTime.UtcNow}
            WHERE Id = {requestId} AND Status = {(int)WorktreeMergeStatus.Completed}
            """);
    }

    public async Task<IReadOnlyList<WorktreeMergeRequest>> ListAsync(string projectSlug)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WorktreeMergeQueue ORDER BY Id";
        return await ReadAllAsync(command);
    }

    public async Task<WorktreeMergeRequest?> GetAsync(string projectSlug, long requestId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        return await ReadByIdAsync(db, requestId);
    }

    public async Task<WorktreeMergeAlertSummary?> GetAlertSummaryAsync(string projectSlug)
    {
        var active = (await ListAsync(projectSlug))
            .Where(row => row.Status != WorktreeMergeStatus.Completed)
            .ToList();
        if (active.Count == 0) return null;
        var mostSevere = active.OrderByDescending(row => Severity(row.Status)).First().Status;
        return new(active.Count, mostSevere, active.Min(row => row.UpdatedAt));
    }

    public async Task<WorktreeMergeRequest?> ProcessNextAsync(string projectSlug, CancellationToken ct)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await EnsureTableAsync(db);
            // A host may stop after claiming. Replaying the same row is safe because integration is verified by ancestry.
            await db.Database.ExecuteSqlRawAsync("UPDATE WorktreeMergeQueue SET Status = 0, Error = 'Recovered after interruption' WHERE Status = 1");
            await ReconcileInterruptedMaintenanceAsync(db);
            var request = await ReadNextAvailableAsync(db, projectSlug, WorktreeMergeStatus.Pending)
                ?? await ReadNextAvailableAsync(db, projectSlug, WorktreeMergeStatus.BlockedByExternalChanges);
            if (request is null) return null;
            await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Preparation);
            return await IntegrateAsync(projectSlug, request, continueRebase: false, ct);
        }
        finally { gate.Release(); }
    }

    private async Task ReconcileInterruptedMaintenanceAsync(TodoDbContext db)
    {
        var interrupted = (await ListMaintenanceWritesAwaitingCommitAsync(db))
            .Where(row => !_activeMaintenanceWrites.ContainsKey(row.Id));
        foreach (var request in interrupted)
        {
            var preparation = PrepareInterruptedMaintenanceWorktree(request.WorktreePath);
            if (preparation.Error is not null)
            {
                await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                    preparation.Error, null);
                continue;
            }

            if (preparation.HasChanges)
            {
                RunGit(request.WorktreePath, ["add", "-A"]);
                var commit = RunGit(request.WorktreePath,
                    ["commit", "-m", "Recover interrupted maintenance write"], false);
                if (commit.ExitCode != 0)
                {
                    await MarkAsync(db, request.Id, WorktreeMergeStatus.CommitPending,
                        $"Recovered files could not be checkpointed: {commit.Error.Trim()}", null);
                    continue;
                }

                var recoveredHead = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Pending},
                        Checkpoint = {(int)WorktreeMergeCheckpoint.Waiting}, SourceCommit = {recoveredHead},
                        Error = 'Recovered and checkpointed interrupted maintenance write',
                        UpdatedAt = {DateTime.UtcNow}
                    WHERE Id = {request.Id}
                    """);
                continue;
            }

            var head = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
            if (string.Equals(head, request.SourceCommit, StringComparison.Ordinal))
            {
                var integrated = IsIntegratedCommit(request.WorktreePath, head, request.TargetBranch);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE WorktreeMergeQueue SET Status = {(int)(integrated
                            ? WorktreeMergeStatus.Completed : WorktreeMergeStatus.Pending)},
                        Checkpoint = {(int)(integrated
                            ? WorktreeMergeCheckpoint.Merge : WorktreeMergeCheckpoint.Waiting)},
                        IntegratedCommit = {(integrated ? head : null)},
                        LocalIntegratedAt = {(integrated ? DateTime.UtcNow : null)},
                        Error = {(integrated ? "Recovered interrupted no-op maintenance write" : "Recovered interrupted maintenance checkpoint")},
                        UpdatedAt = {DateTime.UtcNow}
                    WHERE Id = {request.Id}
                    """);
                continue;
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Pending},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Waiting}, SourceCommit = {head},
                    Error = 'Recovered committed maintenance write after interruption', UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {request.Id}
                """);
        }
    }

    public async Task<WorktreeMergeRequest?> ResumeAsync(string projectSlug, long requestId, CancellationToken ct)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await EnsureTableAsync(db);
            var request = await ReadByIdAsync(db, requestId);
            if (request is null || request.Status is not (WorktreeMergeStatus.Conflict or WorktreeMergeStatus.Failed
                or WorktreeMergeStatus.BlockedByExternalChanges or WorktreeMergeStatus.NeedsReview
                or WorktreeMergeStatus.CommitPending or WorktreeMergeStatus.ValidationRequired
                or WorktreeMergeStatus.Quarantined))
                return null;
            if (request.JobKind == WorktreeMergeJobKind.Ticket
                && await IsBusyAsync(projectSlug, request.RootTicketId))
                return null;
            await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, request.Checkpoint);
            return await IntegrateAsync(projectSlug, request,
                continueRebase: request.Status == WorktreeMergeStatus.Conflict
                    || request.Checkpoint == WorktreeMergeCheckpoint.Rebase,
                ct);
        }
        finally { gate.Release(); }
    }

    private async Task<WorktreeMergeRequest> IntegrateAsync(string slug, WorktreeMergeRequest request, bool continueRebase, CancellationToken ct)
    {
        await using var db = projects.GetProjectDb(slug);
        await EnsureTableAsync(db);
        var project = await projects.GetProjectAsync(slug) ?? throw new InvalidOperationException("Project disappeared.");
        var repository = projects.ResolveRepositoryPath(project);
        try
        {
            var worktreeIsRegistered = IsRegisteredWorktree(repository, request.WorktreePath);
            var target = ResolveIntegrationTarget(repository, request.TargetBranch);
            if (request.JobKind == WorktreeMergeJobKind.Ticket && worktreeIsRegistered)
            {
                var unresolved = ConflictFiles(request.WorktreePath);
                var rebaseInProgress = IsRebaseInProgress(request.WorktreePath);
                if (unresolved.Length > 0 && !rebaseInProgress)
                    return await MarkAsync(db, request.Id, WorktreeMergeStatus.Conflict,
                        "The ticket worktree contains unresolved Git conflicts. Resolve and stage every conflict before resuming; no finalization commit was created.",
                        string.Join('\n', unresolved));
                if (!rebaseInProgress)
                {
                    var markerFiles = ConflictMarkerFiles(request.WorktreePath, target.TipCommit);
                    if (markerFiles.Length > 0)
                        return await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                            "Files containing unresolved conflict markers were preserved. Remove the markers and verify the intended content before resuming: "
                            + string.Join(", ", markerFiles), string.Join('\n', markerFiles));
                    var preparation = PrepareTicketWorktree(request);
                    if (preparation.Error is not null)
                        return await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                            preparation.Error, null);
                    if (preparation.HasChanges)
                    {
                        RunGit(request.WorktreePath, ["add", "-A"]);
                        var commit = RunGit(request.WorktreePath,
                            ["commit", "-m", $"Finalize ticket #{request.RootTicketId} worktree"], false);
                        if (commit.ExitCode != 0)
                            return await MarkAsync(db, request.Id, WorktreeMergeStatus.CommitPending,
                                $"Validated changes could not be committed: {commit.Error.Trim()}", null);
                        if (!IsClean(request.WorktreePath))
                            return await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                                "The finalization commit did not capture the complete worktree; cleanup was stopped.", null);
                        request = request with { SourceCommit = null };
                    }
                }
            }

            var sourceHead = ResolveSourceHead(repository, request.SourceBranch);
            if (sourceHead is not null && !string.Equals(sourceHead, request.SourceCommit, StringComparison.Ordinal))
            {
                await SetSourceCommitAsync(db, request.Id, sourceHead);
                request = request with { SourceCommit = sourceHead };
            }

            // An already-integrated source is proven by ancestry against the integration tip (or the
            // target branch itself when an operator merged externally) and only needs cleanup, which
            // never mutates any checkout's files.
            var alreadyIntegrated = !string.IsNullOrWhiteSpace(request.SourceCommit)
                && IsIntegrated(repository, request.SourceCommit!, request.TargetBranch, target);
            if (!alreadyIntegrated)
            {
                // Integration advances only the integration tip through the dedicated detached-HEAD
                // worktree, so a dirty primary checkout never pauses it. Divergence, however, means
                // the target branch holds external commits the tip does not: advancing the tip past
                // them could publish an integration that can never fast-forward the local checkout.
                if (target.Diverged)
                    return await MarkAsync(db, request.Id, WorktreeMergeStatus.BlockedByExternalChanges,
                        DivergedTipError, null);

                if (!worktreeIsRegistered)
                    return await MarkAsync(db, request.Id, WorktreeMergeStatus.Failed,
                        "The source worktree is no longer registered and its last known commit is not integrated; cleanup was stopped.", null);

                if (continueRebase)
                {
                    await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Rebase);
                    var unresolved = ConflictFiles(request.WorktreePath);
                    if (unresolved.Length > 0)
                    {
                        var resumed = CompleteMemoryOnlyRebase(request.WorktreePath,
                            new GitResult(1, "", "The rebase still contains unresolved conflicts."));
                        if (resumed.ExitCode != 0)
                            return await MarkGitFailureAsync(db, request, resumed);
                    }
                    else if (IsRebaseInProgress(request.WorktreePath))
                    {
                        var continued = RunGit(request.WorktreePath, ["-c", "core.editor=true", "rebase", "--continue"], false);
                        if (continued.ExitCode != 0)
                            continued = CompleteMemoryOnlyRebase(request.WorktreePath, continued);
                        if (continued.ExitCode != 0)
                            return await MarkGitFailureAsync(db, request, continued);
                    }
                    else if (!IsAncestor(repository, target.TipCommit, request.SourceBranch))
                    {
                        var rebased = RunGit(request.WorktreePath, ["rebase", target.TipCommit], false);
                        if (rebased.ExitCode != 0)
                            rebased = CompleteMemoryOnlyRebase(request.WorktreePath, rebased);
                        if (rebased.ExitCode != 0)
                            return await MarkGitFailureAsync(db, request, rebased);
                    }
                }
                else
                {
                    var sourceCommit = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                    await SetSourceCommitAsync(db, request.Id, sourceCommit);
                    request = request with { SourceCommit = sourceCommit };
                    await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Rebase);
                    if (!IsAncestor(repository, target.TipCommit, request.SourceBranch))
                    {
                        var rebased = RunGit(request.WorktreePath, ["rebase", target.TipCommit], false);
                        if (rebased.ExitCode != 0)
                            rebased = CompleteMemoryOnlyRebase(request.WorktreePath, rebased);
                        if (rebased.ExitCode != 0)
                            return await MarkGitFailureAsync(db, request, rebased);
                    }
                }

                // A resumed rebase may have started against an older tip. The tip can legitimately
                // advance while the request waits for conflict resolution, so prove ancestry again
                // before attempting the fast-forward and catch up when necessary.
                target = ResolveIntegrationTarget(repository, request.TargetBranch);
                if (target.Diverged)
                    return await MarkAsync(db, request.Id, WorktreeMergeStatus.BlockedByExternalChanges,
                        DivergedTipError, null);
                if (!IsAncestor(repository, target.TipCommit, request.SourceBranch))
                {
                    var catchUpRebase = RunGit(request.WorktreePath, ["rebase", target.TipCommit], false);
                    if (catchUpRebase.ExitCode != 0)
                        catchUpRebase = CompleteMemoryOnlyRebase(request.WorktreePath, catchUpRebase);
                    if (catchUpRebase.ExitCode != 0)
                        return await MarkGitFailureAsync(db, request, catchUpRebase);
                }

                var rebasedCommit = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                for (var attempt = 0; ; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Merge);
                    beforeFastForward?.Invoke(repository, request.TargetBranch);
                    var integrationWorktree = EnsureIntegrationWorktree(repository, slug, target.TipCommit);
                    var ff = RunGit(integrationWorktree, ["merge", "--ff-only", rebasedCommit], false);
                    string failure;
                    if (ff.ExitCode == 0)
                    {
                        // The target branch may receive external commits while the fast-forward is
                        // validated; re-resolving before publishing keeps them ahead of the new tip.
                        // The compare-and-swap then rejects a tip moved by a concurrent process.
                        var latest = ResolveIntegrationTarget(repository, request.TargetBranch);
                        if (!latest.Diverged && string.Equals(latest.TipCommit, target.TipCommit, StringComparison.Ordinal))
                        {
                            var published = RunGit(repository,
                                ["update-ref", target.TipRef, rebasedCommit, target.RefValue ?? ""], false);
                            if (published.ExitCode == 0)
                                break;
                            failure = published.Error;
                        }
                        else
                            failure = "The integration target advanced while the fast-forward was validated.";
                    }
                    else
                        failure = ff.Error;

                    var refreshed = ResolveIntegrationTarget(repository, request.TargetBranch);
                    if (refreshed.Diverged)
                        return await MarkAsync(db, request.Id, WorktreeMergeStatus.BlockedByExternalChanges,
                            DivergedTipError, null);
                    if (attempt >= MaxTargetAdvanceRetries - 1
                        || (string.Equals(refreshed.TipCommit, target.TipCommit, StringComparison.Ordinal)
                            && string.Equals(refreshed.RefValue, target.RefValue, StringComparison.Ordinal)))
                        throw new InvalidOperationException(failure.Trim());

                    target = refreshed;
                    await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Rebase);
                    var retryRebase = RunGit(request.WorktreePath, ["rebase", target.TipCommit], false);
                    if (retryRebase.ExitCode != 0)
                        retryRebase = CompleteMemoryOnlyRebase(request.WorktreePath, retryRebase);
                    if (retryRebase.ExitCode != 0)
                        return await MarkGitFailureAsync(db, request, retryRebase);
                    rebasedCommit = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                }
                if (!IsIntegratedCommit(repository, rebasedCommit, request.TargetBranch))
                    throw new InvalidOperationException($"Commit {rebasedCommit} is not reachable from the integration tip of {request.TargetBranch}.");
                await SetSourceCommitAsync(db, request.Id, rebasedCommit);
                request = request with { SourceCommit = rebasedCommit };
            }

            var integrated = request.SourceCommit
                ?? ResolveIntegrationTarget(repository, request.TargetBranch).TipCommit;
            if (!IsIntegratedCommit(repository, integrated, request.TargetBranch))
                throw new InvalidOperationException($"Commit {integrated} is not reachable from the integration tip of {request.TargetBranch}; cleanup was stopped.");
            var removeAfterIntegration = request.JobKind == WorktreeMergeJobKind.Ticket
                || request.Status == WorktreeMergeStatus.Quarantined;
            var registeredForCleanup = IsRegisteredWorktree(repository, request.WorktreePath);
            if (registeredForCleanup && !IsClean(request.WorktreePath))
                throw new InvalidOperationException("The source worktree has local changes; cleanup was stopped.");
            if (removeAfterIntegration && registeredForCleanup)
            {
                var removed = RunGit(repository, ["worktree", "remove", request.WorktreePath], false);
                if (removed.ExitCode != 0 && IsRegisteredWorktree(repository, request.WorktreePath))
                    throw new InvalidOperationException($"The source worktree could not be detached: {removed.Error.Trim()}");
            }
            if (removeAfterIntegration
                && RunGit(repository, ["show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}"], false).ExitCode == 0)
            {
                // Integration was proven above against the configured target. A stale upstream tracking
                // ref must not prevent cleanup of the now-detached local ticket branch.
                var deleted = RunGit(repository, ["branch", "-D", request.SourceBranch], false);
                if (deleted.ExitCode != 0 && ResolveSourceHead(repository, request.SourceBranch) is not null)
                    throw new InvalidOperationException($"The integrated source branch could not be deleted: {deleted.Error.Trim()}");
            }
            if (removeAfterIntegration)
                RemoveEmptyResidualDirectory(request.WorktreePath);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Completed},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Merge}, IntegratedCommit = {integrated},
                    LocalIntegratedAt = {DateTime.UtcNow}, Error = NULL, ConflictFiles = NULL,
                    SyncStatus = {(int)LocalCheckoutSyncStatus.Pending}, SyncTargetCommit = {integrated},
                    SyncError = NULL, SyncConflictFiles = NULL, SyncUpdatedAt = {DateTime.UtcNow},
                    UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {request.Id}
                """);
            return (await ReadByIdAsync(db, request.Id))!;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return await MarkAsync(db, request.Id, WorktreeMergeStatus.Failed, ex.Message, null);
        }
    }

    /// <summary>
    /// Reconciles one integrated commit into the configured local checkout. Integration and later
    /// jobs never wait on this step: a conflict remains on its own durable row with a recoverable ref.
    /// </summary>
    public async Task<WorktreeMergeRequest?> SynchronizeNextAsync(string projectSlug, CancellationToken ct)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await EnsureTableAsync(db);
            await db.Database.ExecuteSqlRawAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)LocalCheckoutSyncStatus.Pending}, SyncError = 'Recovered local synchronization after interruption' WHERE SyncStatus = {(int)LocalCheckoutSyncStatus.Processing}");
            var request = await ReadNextSyncAsync(db);
            if (request is null) return null;
            var project = await projects.GetProjectAsync(projectSlug);
            if (project is null) return null;
            var repository = projects.ResolveRepositoryPath(project);
            return await SynchronizeAsync(db, request, repository, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<WorktreeMergeRequest> SynchronizeAsync(
        TodoDbContext db, WorktreeMergeRequest request, string repository, CancellationToken ct)
    {
        if (request.SyncStatus == LocalCheckoutSyncStatus.CleanupPending)
            return await CompleteSyncAsync(db, request.Id, request.SyncTargetCommit!, request.SyncBackupRef, repository);

        if (!Directory.Exists(repository) || RunGit(repository, ["rev-parse", "--show-toplevel"], false).ExitCode != 0)
            return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.CheckoutMissing,
                "The configured local checkout is absent. Integration is safe; restore or reconfigure the checkout, then retry synchronization.", null,
                request.SyncTargetCommit ?? request.IntegratedCommit ?? "unknown", request.SyncBackupRef);
        var target = ResolveIntegrationTarget(repository, request.TargetBranch);
        var desired = target.TipCommit; // always catch up to the newest successful integration

        var branch = RunGit(repository, ["branch", "--show-current"], false).Output.Trim();
        if (!string.Equals(branch, request.TargetBranch, StringComparison.Ordinal))
            return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.Diverged,
                $"The local checkout is on branch '{branch}', not '{request.TargetBranch}'. Check out the target branch and retry synchronization.", null, desired, request.SyncBackupRef);
        var localHead = RunGit(repository, ["rev-parse", "HEAD"]).Output.Trim();
        var alreadyAtDesired = string.Equals(localHead, desired, StringComparison.Ordinal);
        if (alreadyAtDesired && string.IsNullOrWhiteSpace(request.SyncBackupRef))
            return await CompleteSyncAsync(db, request.Id, desired, request.SyncBackupRef, repository);
        if (!alreadyAtDesired && !IsAncestor(repository, localHead, desired))
            return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.Diverged,
                $"Local commits diverge from integration commit {desired}. Reconcile the commits manually; the integrated result remains available at {target.TipRef}.", null, desired, request.SyncBackupRef);

        await SetSyncProcessingAsync(db, request.Id, desired);
        // Everything below may mutate the local checkout (stash, fast-forward, stash apply).
        // Register the window so a concurrent agent run's boundary check can classify the
        // resulting fingerprint drift as a coordinated KittyClaw change, not agent activity.
        using var coordinatedMutation = primaryCheckoutActivity?.BeginCoordinatedMutation(repository);
        var backupRef = request.SyncBackupRef;
        if (string.IsNullOrWhiteSpace(backupRef) && !IsClean(repository))
        {
            var marker = $"KittyClaw local synchronization #{request.Id}";
            var stashed = RunGit(repository, ["stash", "push", "--include-untracked", "--message", marker], false);
            if (stashed.ExitCode != 0)
                return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.ConcurrentChanges,
                    $"Local changes could not be saved safely: {stashed.Error.Trim()}", null, desired, null);
            var stashCommit = RunGit(repository, ["rev-parse", "stash@{0}"], false);
            if (stashCommit.ExitCode != 0)
                return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.ConcurrentChanges,
                    "Local changes were moved to Git's stash but the durable backup could not be identified. Recover the newest stash before retrying.", null, desired, null);
            backupRef = $"refs/kittyclaw/sync-backups/{request.Id}";
            RunGit(repository, ["update-ref", backupRef, stashCommit.Output.Trim()]);
            await SetSyncBackupAsync(db, request.Id, backupRef);
            RunGit(repository, ["stash", "drop", "stash@{0}"]);
        }

        beforeLocalSync?.Invoke(repository);
        ct.ThrowIfCancellationRequested();
        if (!IsClean(repository))
            return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.ConcurrentChanges,
                "The local checkout changed after its backup was captured. The new files were left untouched; retry after external writes stop.", null, desired, backupRef);

        if (!alreadyAtDesired)
        {
            var ff = RunGit(repository, ["merge", "--ff-only", desired], false);
            if (ff.ExitCode != 0)
                return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.ConcurrentChanges,
                    $"The local checkout could not be advanced safely: {ff.Error.Trim()}", null, desired, backupRef);
        }

        if (!string.IsNullOrWhiteSpace(backupRef))
        {
            var restored = RunGit(repository, ["stash", "apply", "--index", backupRef], false);
            if (restored.ExitCode != 0)
            {
                var conflicts = ConflictFiles(repository);
                return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.Conflict,
                    $"The integrated checkout is current, but saved local work conflicted while being restored. Resolve the listed files; the complete backup remains at {backupRef}.",
                    conflicts.Length == 0 ? null : string.Join('\n', conflicts), desired, backupRef);
            }
        }

        var completedState = CaptureCheckoutState(repository);
        beforeLocalSyncCompletion?.Invoke(repository);
        ct.ThrowIfCancellationRequested();
        var finalState = CaptureCheckoutState(repository);
        var finalHead = RunGit(repository, ["rev-parse", "HEAD"], false).Output.Trim();
        if (!string.Equals(completedState, finalState, StringComparison.Ordinal)
            || !string.Equals(finalHead, desired, StringComparison.Ordinal))
            return await MarkSyncAsync(db, request.Id, LocalCheckoutSyncStatus.ConcurrentChanges,
                $"The local checkout files, index, branch, or HEAD changed while synchronization was completing. The external change and recoverable backup at {backupRef ?? "the current checkout"} were preserved; reconcile HEAD with {desired}, then retry after external writes stop.",
                null, desired, backupRef);
        return await CompleteSyncAsync(db, request.Id, desired, backupRef, repository);
    }

    private static string CaptureCheckoutState(string repository)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var visibleFiles = RunGit(repository,
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], false).Output;
        foreach (var relative in visibleFiles.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            try
            {
                var file = Path.Combine(repository, relative);
                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
            catch (IOException)
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes("<concurrent-io>"));
            }
        }
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(RunGit(repository, ["write-tree"], false).Output));
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(RunGit(repository, ["symbolic-ref", "--quiet", "HEAD"], false).Output));
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(RunGit(repository, ["rev-parse", "HEAD"], false).Output));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<WorktreeMergeRequest?> RetrySynchronizationAsync(string projectSlug, long requestId, CancellationToken ct)
    {
        var gate = ProjectGates.GetOrAdd(projectSlug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = projects.GetProjectDb(projectSlug);
            await EnsureTableAsync(db);
            var request = await ReadByIdAsync(db, requestId);
            if (request is null || request.SyncStatus is not (LocalCheckoutSyncStatus.Conflict
                or LocalCheckoutSyncStatus.Diverged or LocalCheckoutSyncStatus.CheckoutMissing
                or LocalCheckoutSyncStatus.ConcurrentChanges)) return null;
            var project = await projects.GetProjectAsync(projectSlug);
            if (project is null) return null;
            var repository = projects.ResolveRepositoryPath(project);
            // Completing in place is only safe while the reconciled work is still visible in the
            // working tree. A checkout reset clean to the target must take the full path below so
            // the durable backup is re-applied instead of silently deleted.
            if (request.SyncStatus == LocalCheckoutSyncStatus.Conflict && Directory.Exists(repository)
                && ConflictFiles(repository).Length == 0
                && !IsClean(repository)
                && string.Equals(RunGit(repository, ["rev-parse", "HEAD"], false).Output.Trim(), request.SyncTargetCommit, StringComparison.Ordinal))
                return await CompleteSyncAsync(db, request.Id, request.SyncTargetCommit!, request.SyncBackupRef, repository);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)LocalCheckoutSyncStatus.Pending}, SyncError = 'Local synchronization retry requested', SyncUpdatedAt = {DateTime.UtcNow} WHERE Id = {requestId}");
            request = (await ReadByIdAsync(db, requestId))!;
            return await SynchronizeAsync(db, request, repository, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<WorktreeMergeRequest> CompleteSyncAsync(TodoDbContext db, long id, string desired, string? backupRef, string repository)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)LocalCheckoutSyncStatus.CleanupPending}, SyncTargetCommit = {desired}, SyncError = NULL, SyncConflictFiles = NULL, SyncUpdatedAt = {now}, UpdatedAt = {now} WHERE Id = {id}");
        afterSyncCompletionPersisted?.Invoke(repository);
        if (!string.IsNullOrWhiteSpace(backupRef))
        {
            var deleted = RunGit(repository, ["update-ref", "-d", backupRef], false);
            if (deleted.ExitCode != 0)
            {
                var error = $"The local work was restored, but its recoverable backup could not be cleaned up: {deleted.Error.Trim()}. Retry cleanup; the checkout is already synchronized.";
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncError = {error}, SyncUpdatedAt = {DateTime.UtcNow}, UpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");
                return (await ReadByIdAsync(db, id))!;
            }
        }
        now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)LocalCheckoutSyncStatus.Completed}, SyncTargetCommit = {desired}, SyncBackupRef = NULL, SyncError = NULL, SyncConflictFiles = NULL, SyncUpdatedAt = {now}, UpdatedAt = {now} WHERE Id = {id}");
        return (await ReadByIdAsync(db, id))!;
    }

    private static async Task<WorktreeMergeRequest> MarkSyncAsync(TodoDbContext db, long id, LocalCheckoutSyncStatus status, string error, string? conflicts, string target, string? backupRef)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)status}, SyncTargetCommit = {target}, SyncBackupRef = {backupRef}, SyncError = {error}, SyncConflictFiles = {conflicts}, SyncUpdatedAt = {now}, UpdatedAt = {now} WHERE Id = {id}");
        return (await ReadByIdAsync(db, id))!;
    }

    private static Task SetSyncProcessingAsync(TodoDbContext db, long id, string target) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncStatus = {(int)LocalCheckoutSyncStatus.Processing}, SyncTargetCommit = {target}, SyncUpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");

    private static Task SetSyncBackupAsync(TodoDbContext db, long id, string backupRef) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SyncBackupRef = {backupRef}, SyncUpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");

    private static bool IsAncestor(string repository, string commit, string target) =>
        RunGit(repository, ["merge-base", "--is-ancestor", commit, target], false).ExitCode == 0;

    /// <summary>
    /// Durable ref recording how far integrations have advanced for a target branch. It is never a
    /// checked-out branch, so publishing it cannot desynchronize any checkout; the local checkout
    /// catches up during the separate synchronization step.
    /// </summary>
    public static string IntegrationTipRef(string targetBranch) => $"refs/kittyclaw/integration/{targetBranch}";

    private sealed record IntegrationTarget(string TipRef, string? RefValue, string TipCommit, bool Diverged);

    private static IntegrationTarget? TryResolveIntegrationTarget(string gitPath, string targetBranch)
    {
        var head = RunGit(gitPath, ["rev-parse", "--verify", $"refs/heads/{targetBranch}"], false);
        if (head.ExitCode != 0) return null;
        var targetHead = head.Output.Trim();
        var tipRef = IntegrationTipRef(targetBranch);
        var recorded = RunGit(gitPath, ["rev-parse", "--verify", tipRef], false);
        var refValue = recorded.ExitCode == 0 ? recorded.Output.Trim() : null;
        if (refValue is null || string.Equals(refValue, targetHead, StringComparison.Ordinal)
            || IsAncestor(gitPath, refValue, targetHead))
            return new(tipRef, refValue, targetHead, false);
        if (IsAncestor(gitPath, targetHead, refValue))
            return new(tipRef, refValue, refValue, false);
        return new(tipRef, refValue, refValue, true);
    }

    private static IntegrationTarget ResolveIntegrationTarget(string gitPath, string targetBranch) =>
        TryResolveIntegrationTarget(gitPath, targetBranch)
        ?? throw new InvalidOperationException($"The integration branch '{targetBranch}' does not exist.");

    private static bool IsIntegrated(string gitPath, string commit, string targetBranch, IntegrationTarget target) =>
        IsAncestor(gitPath, commit, target.TipCommit) || IsAncestor(gitPath, commit, targetBranch);

    private static bool IsIntegratedCommit(string gitPath, string commit, string targetBranch) =>
        TryResolveIntegrationTarget(gitPath, targetBranch) is { } target
        && IsIntegrated(gitPath, commit, targetBranch, target);

    /// <summary>
    /// The dedicated per-project integration worktree. It always rides a detached HEAD so it never
    /// claims branch ownership, and it only ever materializes commits that are already durable,
    /// so resetting it to the current tip can never destroy the sole copy of any change.
    /// </summary>
    private static string EnsureIntegrationWorktree(string repository, string projectSlug, string tipCommit)
    {
        var parent = Directory.GetParent(repository)?.FullName
            ?? throw new InvalidOperationException($"Repository '{repository}' has no parent directory.");
        var repositoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(repository));
        var path = Path.GetFullPath(Path.Combine(parent, $"{repositoryName}.worktrees", $"integration-{SafeName(projectSlug)}"));
        if (IsRegisteredWorktree(repository, path) && !Directory.Exists(path))
            RunGit(repository, ["worktree", "prune"], false);
        if (!IsRegisteredWorktree(repository, path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RunGit(repository, ["worktree", "add", "--detach", path, tipCommit]);
            return path;
        }
        var headRef = RunGit(path, ["rev-parse", "--abbrev-ref", "HEAD"]).Output.Trim();
        if (!string.Equals(headRef, "HEAD", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The integration worktree '{path}' unexpectedly has branch '{headRef}' checked out; remove the worktree before resuming.");
        RunGit(path, ["reset", "--hard", tipCommit]);
        return path;
    }

    private static string SafeName(string value) =>
        new(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    private static bool IsRebaseInProgress(string worktreePath)
    {
        foreach (var stateDirectory in new[] { "rebase-merge", "rebase-apply" })
        {
            var result = RunGit(worktreePath, ["rev-parse", "--git-path", stateDirectory], false);
            if (result.ExitCode != 0) continue;
            var path = result.Output.Trim();
            if (!Path.IsPathRooted(path)) path = Path.Combine(worktreePath, path);
            if (Directory.Exists(path)) return true;
        }
        return false;
    }

    private static string? ResolveSourceHead(string repository, string sourceBranch)
    {
        var result = RunGit(repository, ["rev-parse", "--verify", $"refs/heads/{sourceBranch}"], false);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private static bool IsRegisteredWorktree(string repository, string worktreePath)
    {
        var result = RunGit(repository, ["worktree", "list", "--porcelain"], false);
        if (result.ExitCode != 0) return false;
        var expected = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => Path.GetFullPath(line[9..].Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static TicketWorktreeState InspectRegisteredWorktree(TicketWorktree worktree, int rootTicketId)
    {
        if (!Directory.Exists(worktree.Path))
            return new(worktree.Path, worktree.Branch, rootTicketId, false, false, null);
        var branch = RunGit(worktree.Path, ["branch", "--show-current"], false);
        if (branch.ExitCode != 0)
            return new(worktree.Path, worktree.Branch, rootTicketId, true, false, branch.Error.Trim());
        if (!string.Equals(branch.Output.Trim(), worktree.Branch, StringComparison.Ordinal))
            return new(worktree.Path, worktree.Branch, rootTicketId, true, false,
                $"Registered on {branch.Output.Trim()}, expected {worktree.Branch}.");
        var status = RunGit(worktree.Path, ["status", "--porcelain"], false);
        return new(worktree.Path, worktree.Branch, rootTicketId, true,
            !string.IsNullOrWhiteSpace(status.Output), status.ExitCode == 0 ? null : status.Error.Trim());
    }

    private static void RemoveEmptyResidualDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        if (Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("The detached worktree directory still contains files; it was preserved for review.");
        try { Directory.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<bool> IsBusyAsync(string projectSlug, int rootTicketId)
    {
        if (finalization?.IsBusy(projectSlug, rootTicketId) == true) return true;
        if (runs is null) return false;
        foreach (var run in runs.ActiveForProject(projectSlug))
        {
            if (run.TicketId is not int ticketId) continue;
            try
            {
                if (await worktrees.ResolveRootTicketIdAsync(projectSlug, ticketId) == rootTicketId)
                    return true;
            }
            catch (InvalidOperationException) { }
        }
        return false;
    }

    private static WorktreePreparation PrepareTicketWorktree(WorktreeMergeRequest request)
    {
        var status = RunGit(request.WorktreePath, ["status", "--porcelain", "-z", "--untracked-files=all"]).Output;
        var blocked = new List<string>();
        var hasChanges = false;
        foreach (var entry in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Length < 4) continue;
            var path = entry[3..].Replace('\\', '/');
            if (IsSensitive(path))
            {
                blocked.Add(path + " (local-only or potentially sensitive path)");
                continue;
            }
            var fullPath = Path.GetFullPath(Path.Combine(request.WorktreePath, path));
            if (!fullPath.StartsWith(Path.GetFullPath(request.WorktreePath) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add(path + " (outside worktree)");
                continue;
            }
            if (entry.StartsWith("??", StringComparison.Ordinal) && IsRecognizedTemporary(path))
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                continue;
            }
            if (ProbableSecretScanner.ContainsProbableSecret(fullPath))
            {
                blocked.Add(path + " (possible secret content)");
                continue;
            }
            hasChanges = true;
        }
        return blocked.Count == 0
            ? new(hasChanges, null)
            : new(hasChanges, "Unexpected or potentially sensitive files were preserved and require review: " + string.Join(", ", blocked));
    }

    private static WorktreePreparation PrepareInterruptedMaintenanceWorktree(string worktreePath)
    {
        var status = RunGit(worktreePath,
            ["status", "--porcelain", "-z", "--untracked-files=all"], false);
        if (status.ExitCode != 0)
            return new(false,
                $"Interrupted maintenance worktree could not be inspected: {status.Error.Trim()}");

        var blocked = new List<string>();
        var hasChanges = false;
        foreach (var entry in status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Length < 4) continue;
            var path = entry[3..].Replace('\\', '/');
            var fullPath = Path.GetFullPath(Path.Combine(worktreePath, path));
            if (!fullPath.StartsWith(Path.GetFullPath(worktreePath) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                blocked.Add(path + " (outside worktree)");
                continue;
            }
            if (entry.StartsWith("??", StringComparison.Ordinal) && IsRecognizedTemporary(path))
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                continue;
            }
            if (IsSensitive(path) || ProbableSecretScanner.ContainsProbableSecret(fullPath))
            {
                blocked.Add(path + " (local-only or potentially sensitive)");
                continue;
            }
            hasChanges = true;
        }

        return blocked.Count == 0
            ? new(hasChanges, null)
            : new(hasChanges,
                "Interrupted maintenance files require quarantine: " + string.Join(", ", blocked));
    }

    private static bool IsRecognizedTemporary(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSensitive(string path)
    {
        var name = Path.GetFileName(path);
        var normalized = '/' + path.Replace('\\', '/').Trim('/') + '/';
        return LocalOnlyPathRegex().IsMatch(normalized)
            || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"/(transcripts?|prompts?|sessions?|traces?|secrets?)/|/(\.env|credentials?[^/]*)/|\.(pem|key)/$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalOnlyPathRegex();

    private sealed record WorktreePreparation(bool HasChanges, string? Error);

    private static void RequireClean(string path, string label)
    {
        var status = RunGit(path, ["status", "--porcelain"]).Output;
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException($"The {label} '{path}' has uncommitted changes; nothing was modified.");
    }

    private static bool IsClean(string path) =>
        string.IsNullOrWhiteSpace(RunGit(path, ["status", "--porcelain"]).Output);

    private static int Severity(WorktreeMergeStatus status) => status switch
    {
        WorktreeMergeStatus.Conflict => 5,
        WorktreeMergeStatus.Quarantined => 5,
        WorktreeMergeStatus.NeedsReview => 4,
        WorktreeMergeStatus.Failed => 4,
        WorktreeMergeStatus.BlockedByExternalChanges => 3,
        WorktreeMergeStatus.ValidationRequired => 2,
        WorktreeMergeStatus.CommitPending => 2,
        WorktreeMergeStatus.Processing => 1,
        _ => 0
    };

    private static string[] ConflictFiles(string path) => RunGit(path, ["diff", "--name-only", "--diff-filter=U"], false)
        .Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static GitResult CompleteMemoryOnlyRebase(string worktreePath, GitResult failure)
    {
        for (var attempt = 0; attempt < 100 && failure.ExitCode != 0; attempt++)
        {
            var conflicts = ConflictFiles(worktreePath);
            if (conflicts.Length == 0 || conflicts.Any(path => !IsAppendOnlyProcessorMemory(path)))
                return failure;
            if (!TryResolveAppendOnlyMemoryConflicts(worktreePath, conflicts))
                return failure;

            failure = RunGit(worktreePath,
                ["-c", "core.editor=true", "rebase", "--continue"], false);
        }

        return failure;
    }

    private static bool IsAppendOnlyProcessorMemory(string path) => Regex.IsMatch(
        path.Replace('\\', '/'),
        @"^\.agents/processors/column-\d+/memory/(?:MEMORY\.md|pipeline-lessons\.md)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool TryResolveAppendOnlyMemoryConflicts(string worktreePath, IReadOnlyList<string> conflicts)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"KittyClaw-memory-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            for (var index = 0; index < conflicts.Count; index++)
            {
                var relativePath = conflicts[index].Replace('\\', '/');
                var oursPath = Path.Combine(temporaryDirectory, $"{index}-ours.md");
                var baselinePath = Path.Combine(temporaryDirectory, $"{index}-base.md");
                var theirsPath = Path.Combine(temporaryDirectory, $"{index}-theirs.md");
                if (!TryWriteGitOutputToFile(worktreePath, ["show", $":2:{relativePath}"], oursPath)
                    || !TryWriteGitOutputToFile(worktreePath, ["show", $":3:{relativePath}"], theirsPath))
                    return false;
                if (!TryWriteGitOutputToFile(worktreePath, ["show", $":1:{relativePath}"], baselinePath))
                    File.WriteAllBytes(baselinePath, []);

                var merged = RunGit(worktreePath,
                    ["merge-file", "--union", oursPath, baselinePath, theirsPath], false);
                if (merged.ExitCode != 0 || ContainsConflictMarkers(oursPath))
                    return false;

                var destination = Path.GetFullPath(Path.Combine(worktreePath, relativePath));
                var root = Path.GetFullPath(worktreePath) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return false;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(oursPath, destination, true);
                if (RunGit(worktreePath, ["add", "--", relativePath], false).ExitCode != 0)
                    return false;
            }

            return true;
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    private static bool TryWriteGitOutputToFile(
        string worktreePath, IReadOnlyList<string> arguments, string destination)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = worktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git could not be started.");
        using (var output = File.Create(destination))
            process.StandardOutput.BaseStream.CopyTo(output);
        _ = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(true);
            throw new InvalidOperationException("Git command timed out.");
        }
        return process.ExitCode == 0;
    }

    private static string[] ConflictMarkerFiles(string worktreePath, string integrationTip)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var committed = RunGit(worktreePath, ["diff", "--name-only", "-z", $"{integrationTip}...HEAD"], false);
        foreach (var path in committed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(path.Replace('\\', '/'));
        var status = RunGit(worktreePath, ["status", "--porcelain", "-z", "--untracked-files=all"], false);
        foreach (var entry in status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (entry.Length >= 4)
                candidates.Add(entry[3..].Replace('\\', '/'));

        var root = Path.GetFullPath(worktreePath) + Path.DirectorySeparatorChar;
        return candidates.Where(path =>
            {
                var fullPath = Path.GetFullPath(Path.Combine(worktreePath, path));
                return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && ContainsConflictMarkers(fullPath);
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ContainsConflictMarkers(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return false;
        try
        {
            var left = false;
            var separator = false;
            var right = false;
            foreach (var line in File.ReadLines(path))
            {
                left |= line.StartsWith("<<<<<<< ", StringComparison.Ordinal);
                separator |= line.Equals("=======", StringComparison.Ordinal);
                right |= line.StartsWith(">>>>>>> ", StringComparison.Ordinal);
                if (left && separator && right) return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static async Task<WorktreeMergeRequest> MarkGitFailureAsync(TodoDbContext db, WorktreeMergeRequest request, GitResult result)
    {
        var conflicts = ConflictFiles(request.WorktreePath);
        return await MarkAsync(db, request.Id,
            conflicts.Length > 0 ? WorktreeMergeStatus.Conflict : WorktreeMergeStatus.Failed,
            result.Error.Trim(), conflicts.Length == 0 ? null : string.Join('\n', conflicts));
    }

    private static async Task<WorktreeMergeRequest> MarkAsync(TodoDbContext db, long id, WorktreeMergeStatus status, string? error, string? conflicts)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Error = {error}, ConflictFiles = {conflicts}, UpdatedAt = CASE WHEN Status = {(int)status} THEN UpdatedAt ELSE {DateTime.UtcNow} END, Status = {(int)status} WHERE Id = {id}");
        return (await ReadByIdAsync(db, id))!;
    }

    private static Task UpdateAsync(TodoDbContext db, long id, WorktreeMergeStatus status) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = {(int)status}, UpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");

    private static Task SetStateAsync(TodoDbContext db, long id, WorktreeMergeStatus status, WorktreeMergeCheckpoint checkpoint) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = {(int)status}, Checkpoint = {(int)checkpoint}, UpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");

    private static Task SetSourceCommitAsync(TodoDbContext db, long id, string commit) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SourceCommit = {commit}, UpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");

    private async Task<WorktreeMergeRequest?> ReadNextAvailableAsync(
        TodoDbContext db, string projectSlug, WorktreeMergeStatus status)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WorktreeMergeQueue WHERE Status = $status ORDER BY Id";
        command.Parameters.AddWithValue("$status", (int)status);
        foreach (var request in await ReadAllAsync(command))
        {
            if (request.JobKind != WorktreeMergeJobKind.Ticket
                || !await IsBusyAsync(projectSlug, request.RootTicketId))
                return request;
        }
        return null;
    }
    private static Task<WorktreeMergeRequest?> ReadByIdAsync(TodoDbContext db, long id) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE Id = $id", ("$id", id));
    private static Task<WorktreeMergeRequest?> ReadByRootAsync(TodoDbContext db, int root) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE RootTicketId = $root ORDER BY Id DESC LIMIT 1", ("$root", root));

    private static async Task<WorktreeMergeRequest?> ReadNextSyncAsync(TodoDbContext db)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WorktreeMergeQueue WHERE SyncStatus IN ($pending, $cleanup) ORDER BY CASE SyncStatus WHEN $cleanup THEN 0 ELSE 1 END, Id LIMIT 1";
        command.Parameters.AddWithValue("$pending", (int)LocalCheckoutSyncStatus.Pending);
        command.Parameters.AddWithValue("$cleanup", (int)LocalCheckoutSyncStatus.CleanupPending);
        return (await ReadAllAsync(command)).SingleOrDefault();
    }

    // The partial unique index guarantees at most one such row; SingleOrDefault asserts it.
    private static Task<WorktreeMergeRequest?> ReadActiveMaintenanceAsync(TodoDbContext db) =>
        ReadSingleAsync(db, $"""
            SELECT * FROM WorktreeMergeQueue WHERE RootTicketId = $root
                AND Status NOT IN ({(int)WorktreeMergeStatus.Completed}, {(int)WorktreeMergeStatus.Quarantined})
            """, ("$root", int.MinValue));

    private static async Task<List<WorktreeMergeRequest>> ListMaintenanceWritesAwaitingCommitAsync(TodoDbContext db)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM WorktreeMergeQueue
            WHERE JobKind = $kind AND Status IN ($commitPending, $validationRequired)
            ORDER BY Id
            """;
        command.Parameters.AddWithValue("$kind", (int)WorktreeMergeJobKind.Maintenance);
        command.Parameters.AddWithValue("$commitPending", (int)WorktreeMergeStatus.CommitPending);
        command.Parameters.AddWithValue("$validationRequired", (int)WorktreeMergeStatus.ValidationRequired);
        return await ReadAllAsync(command);
    }

    private static async Task<WorktreeMergeRequest?> ReadSingleAsync(TodoDbContext db, string sql, (string, object) parameter)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Item1, parameter.Item2);
        return (await ReadAllAsync(command)).SingleOrDefault();
    }

    private static async Task<List<WorktreeMergeRequest>> ReadAllAsync(SqliteCommand command)
    {
        var rows = new List<WorktreeMergeRequest>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(new(
            reader.GetInt64(reader.GetOrdinal("Id")), reader.GetInt32(reader.GetOrdinal("TicketId")),
            reader.GetInt32(reader.GetOrdinal("RootTicketId")), reader.GetString(reader.GetOrdinal("WorktreePath")),
            reader.GetString(reader.GetOrdinal("SourceBranch")), reader.GetString(reader.GetOrdinal("TargetBranch")),
            (WorktreeMergeStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            reader.GetDateTime(reader.GetOrdinal("CreatedAt")), reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
            GetNullable(reader, "SourceCommit"), GetNullable(reader, "IntegratedCommit"),
            GetNullable(reader, "Error"), GetNullable(reader, "ConflictFiles"),
            (WorktreeMergeJobKind)reader.GetInt32(reader.GetOrdinal("JobKind")),
            (WorktreeMergeCheckpoint)reader.GetInt32(reader.GetOrdinal("Checkpoint")),
            GetNullableDateTime(reader, "LocalIntegratedAt"), GetNullableDateTime(reader, "RemotePublishedAt"),
            (LocalCheckoutSyncStatus)reader.GetInt32(reader.GetOrdinal("SyncStatus")),
            GetNullable(reader, "SyncTargetCommit"), GetNullable(reader, "SyncBackupRef"),
            GetNullable(reader, "SyncError"), GetNullable(reader, "SyncConflictFiles"),
            GetNullableDateTime(reader, "SyncUpdatedAt")));
        return rows;
    }

    private static string? GetNullable(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? GetNullableDateTime(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    private static GitResult RunGit(string path, IReadOnlyList<string> args, bool throwOnError = true)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new InvalidOperationException("Git command timed out."); }
        var result = new GitResult(process.ExitCode, output, error);
        if (throwOnError && result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Trim()}");
        return result;
    }

    public async Task<(WorktreeMergeRequest? Request, int? Position)> GetForTicketAsync(string projectSlug, int ticketId)
    {
        var root = await worktrees.ResolveRootTicketIdAsync(projectSlug, ticketId);
        var rows = await ListAsync(projectSlug);
        var request = rows.LastOrDefault(row => row.RootTicketId == root);
        if (request?.Status != WorktreeMergeStatus.Pending) return (request, null);
        var position = rows.Where(row => row.Status == WorktreeMergeStatus.Pending && row.Id <= request.Id).Count();
        return (request, position);
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
