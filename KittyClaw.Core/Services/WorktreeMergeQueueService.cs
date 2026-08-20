using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
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
    BlockedByExternalChanges = 8
}

public enum WorktreeMergeJobKind { Ticket = 0, Maintenance = 1 }
public enum WorktreeMergeCheckpoint { Preparation = 0, Writing = 1, Validation = 2, Commit = 3, Waiting = 4, Rebase = 5, Merge = 6 }

public sealed record WorktreeMergeRequest(
    long Id, int TicketId, int RootTicketId, string WorktreePath, string SourceBranch,
    string TargetBranch, WorktreeMergeStatus Status, DateTime CreatedAt, DateTime UpdatedAt,
    string? SourceCommit, string? IntegratedCommit, string? Error, string? ConflictFiles,
    WorktreeMergeJobKind JobKind = WorktreeMergeJobKind.Ticket,
    WorktreeMergeCheckpoint Checkpoint = WorktreeMergeCheckpoint.Preparation,
    DateTime? LocalIntegratedAt = null, DateTime? RemotePublishedAt = null);

public sealed record WorktreeMergeAlertSummary(
    int ActiveCount, WorktreeMergeStatus MostSevereStatus, DateTime OldestUpdatedAt);

/// <summary>Durable, per-project serialized integration queue for canonical ticket worktrees.</summary>
public sealed partial class WorktreeMergeQueueService(
    ProjectService projects,
    TicketWorktreeService worktrees,
    WorktreeFinalizationCoordinator? finalization = null,
    AgentRunRegistry? runs = null)
{
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
    }

    public async Task<WorktreeMergeRequest> EnqueueAsync(string projectSlug, int ticketId, CancellationToken ct)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        if (!project.WorktreesEnabled || string.IsNullOrWhiteSpace(project.IntegrationBranch))
            throw new InvalidOperationException("Worktree integration is not enabled for this project.");
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var rootTicketId = await worktrees.ResolveRootTicketIdAsync(projectSlug, ticketId);
        var existing = await ReadByRootAsync(db, rootTicketId);
        if (existing is not null) return existing;
        var worktree = await worktrees.ResolveAsync(projectSlug, ticketId, ct)
            ?? throw new InvalidOperationException("The ticket has no canonical worktree.");
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
    /// Discovers canonical worktrees left behind for root tickets that are already terminal.
    /// Inspection is deliberately non-creating: recovery must never manufacture a worktree for
    /// an old completed ticket merely because its queue row is absent.
    /// </summary>
    public async Task<int> RecoverTerminalWorktreesAsync(string projectSlug, CancellationToken ct)
    {
        var project = await projects.GetProjectAsync(projectSlug);
        if (project is null || !project.WorktreesEnabled || string.IsNullOrWhiteSpace(project.IntegrationBranch))
            return 0;

        var terminalRoots = (await worktrees.ListTerminalRootTicketsAsync(projectSlug))
            .Distinct()
            .ToList();
        var recovered = 0;
        foreach (var ticketId in terminalRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsBusyAsync(projectSlug, ticketId)) continue;
            var state = await worktrees.InspectAsync(projectSlug, ticketId);
            if (state is not { Exists: true }) continue;
            await EnqueueAsync(projectSlug, ticketId, ct);
            recovered++;
        }
        return recovered;
    }

    public async Task<WorktreeMergeRequest> PrepareMaintenanceAsync(
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
        var existing = await ReadByRootAsync(db, maintenanceRoot);
        if (existing is not null && existing.Status != WorktreeMergeStatus.Completed)
        {
            _activeMaintenanceWrites.TryAdd(existing.Id, 0);
            return existing;
        }
        var now = DateTime.UtcNow;
        var baselineCommit = RunGit(worktreePath, ["rev-parse", "HEAD"]).Output.Trim();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorktreeMergeQueue
                (TicketId, RootTicketId, WorktreePath, SourceBranch, TargetBranch, Status, CreatedAt, UpdatedAt, SourceCommit, JobKind, Checkpoint)
            VALUES ({0}, {maintenanceRoot}, {worktreePath}, {sourceBranch}, {project.IntegrationBranch},
                {(int)WorktreeMergeStatus.CommitPending}, {now}, {now}, {baselineCommit},
                {(int)WorktreeMergeJobKind.Maintenance}, {(int)WorktreeMergeCheckpoint.Writing})
            """);
        var created = (await ReadByRootAsync(db, maintenanceRoot))!;
        _activeMaintenanceWrites.TryAdd(created.Id, 0);
        return created;
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
            var request = await ReadNextAsync(db, WorktreeMergeStatus.Pending)
                ?? await ReadNextAsync(db, WorktreeMergeStatus.BlockedByExternalChanges);
            if (request is null) return null;
            if (request.JobKind == WorktreeMergeJobKind.Ticket
                && await IsBusyAsync(projectSlug, request.RootTicketId))
                return null;
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
            var dirty = RunGit(request.WorktreePath, ["status", "--porcelain"]).Output;
            if (!string.IsNullOrWhiteSpace(dirty))
            {
                await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                    "Maintenance writing was interrupted with preserved uncommitted changes; review and commit them before resuming.", null);
                continue;
            }

            var head = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
            if (string.Equals(head, request.SourceCommit, StringComparison.Ordinal))
            {
                await MarkAsync(db, request.Id, WorktreeMergeStatus.NeedsReview,
                    "Maintenance writing was interrupted before a new commit was created; the worktree was preserved.", null);
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
                or WorktreeMergeStatus.CommitPending or WorktreeMergeStatus.ValidationRequired))
                return null;
            if (request.JobKind == WorktreeMergeJobKind.Ticket
                && await IsBusyAsync(projectSlug, request.RootTicketId))
                return null;
            await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, request.Checkpoint);
            return await IntegrateAsync(projectSlug, request, continueRebase: request.Status == WorktreeMergeStatus.Conflict, ct);
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
            if (!IsClean(repository))
                return await MarkAsync(db, request.Id, WorktreeMergeStatus.BlockedByExternalChanges,
                    "The target branch checkout has local changes. Runs can continue, but integration is paused until those external changes are resolved.", null);
            var checkedOut = RunGit(repository, ["branch", "--show-current"]).Output.Trim();
            if (!string.Equals(checkedOut, request.TargetBranch, StringComparison.Ordinal))
                throw new InvalidOperationException($"Integration checkout is on '{checkedOut}', expected '{request.TargetBranch}'.");

            if (request.JobKind == WorktreeMergeJobKind.Ticket && Directory.Exists(request.WorktreePath))
            {
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

            var alreadyIntegrated = !string.IsNullOrWhiteSpace(request.SourceCommit)
                && RunGit(repository, ["merge-base", "--is-ancestor", request.SourceCommit!, request.TargetBranch], false).ExitCode == 0;
            if (!alreadyIntegrated)
            {
                if (continueRebase)
                {
                    await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Rebase);
                    var unresolved = ConflictFiles(request.WorktreePath);
                    if (unresolved.Length > 0)
                        return await MarkAsync(db, request.Id, WorktreeMergeStatus.Conflict,
                            "Resolve and stage all conflict files before resuming.", string.Join('\n', unresolved));
                    var continued = RunGit(request.WorktreePath, ["-c", "core.editor=true", "rebase", "--continue"], false);
                    if (continued.ExitCode != 0)
                        return await MarkGitFailureAsync(db, request, continued);
                }
                else
                {
                    var sourceCommit = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                    await SetSourceCommitAsync(db, request.Id, sourceCommit);
                    request = request with { SourceCommit = sourceCommit };
                    await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Rebase);
                    var rebased = RunGit(request.WorktreePath, ["rebase", request.TargetBranch], false);
                    if (rebased.ExitCode != 0)
                        return await MarkGitFailureAsync(db, request, rebased);
                }

                var rebasedCommit = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                await SetStateAsync(db, request.Id, WorktreeMergeStatus.Processing, WorktreeMergeCheckpoint.Merge);
                var ff = RunGit(repository, ["merge", "--ff-only", request.SourceBranch], false);
                if (ff.ExitCode != 0) throw new InvalidOperationException(ff.Error.Trim());
                if (RunGit(repository, ["merge-base", "--is-ancestor", rebasedCommit, request.TargetBranch], false).ExitCode != 0)
                    throw new InvalidOperationException($"Commit {rebasedCommit} is not reachable from {request.TargetBranch}.");
                await SetSourceCommitAsync(db, request.Id, rebasedCommit);
                request = request with { SourceCommit = rebasedCommit };
            }

            var integrated = request.SourceCommit ?? RunGit(repository, ["rev-parse", request.TargetBranch]).Output.Trim();
            if (Directory.Exists(request.WorktreePath))
                RunGit(repository, ["worktree", "remove", request.WorktreePath]);
            if (RunGit(repository, ["show-ref", "--verify", "--quiet", $"refs/heads/{request.SourceBranch}"], false).ExitCode == 0)
                RunGit(repository, ["branch", "-d", request.SourceBranch]);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Completed},
                    Checkpoint = {(int)WorktreeMergeCheckpoint.Merge}, IntegratedCommit = {integrated},
                    LocalIntegratedAt = {DateTime.UtcNow}, Error = NULL, ConflictFiles = NULL, UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {request.Id}
                """);
            return (await ReadByIdAsync(db, request.Id))!;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return await MarkAsync(db, request.Id, WorktreeMergeStatus.Failed, ex.Message, null);
        }
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
            if (entry.StartsWith("??", StringComparison.Ordinal) && !IsDurableMemory(path))
            {
                blocked.Add(path + " (unexpected untracked path)");
                continue;
            }
            if (ContainsProbableSecret(fullPath))
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

    private static bool IsDurableMemory(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4
            && segments[0].Equals(".agents", StringComparison.OrdinalIgnoreCase)
            && segments.Skip(1).Take(segments.Length - 2)
                .Any(segment => segment.Equals("memory", StringComparison.OrdinalIgnoreCase));
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

    private static bool ContainsProbableSecret(string path) => File.Exists(path)
        && new FileInfo(path).Length <= 1024 * 1024
        && SecretContentRegex().IsMatch(File.ReadAllText(path));

    [GeneratedRegex(@"/(transcripts?|prompts?|sessions?|traces?|secrets?)/|/(\.env|credentials?[^/]*)/|\.(pem|key)/$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalOnlyPathRegex();

    [GeneratedRegex("(?i)(api[_-]?key|access[_-]?token|client[_-]?secret|password|private[_-]?key)\\s*[:=]\\s*['\\\"]?[A-Za-z0-9_\\-/+=]{8,}")]
    private static partial Regex SecretContentRegex();

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

    private static Task<WorktreeMergeRequest?> ReadNextAsync(TodoDbContext db, WorktreeMergeStatus status) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE Status = $status ORDER BY Id LIMIT 1", ("$status", (int)status));
    private static Task<WorktreeMergeRequest?> ReadByIdAsync(TodoDbContext db, long id) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE Id = $id", ("$id", id));
    private static Task<WorktreeMergeRequest?> ReadByRootAsync(TodoDbContext db, int root) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE RootTicketId = $root ORDER BY Id DESC LIMIT 1", ("$root", root));

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
            GetNullableDateTime(reader, "LocalIntegratedAt"), GetNullableDateTime(reader, "RemotePublishedAt")));
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
