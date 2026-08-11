using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using KittyClaw.Core.Data;

namespace KittyClaw.Core.Services;

public enum WorktreeMergeStatus { Pending, Processing, Conflict, Failed, Completed }

public sealed record WorktreeMergeRequest(
    long Id, int TicketId, int RootTicketId, string WorktreePath, string SourceBranch,
    string TargetBranch, WorktreeMergeStatus Status, DateTime CreatedAt, DateTime UpdatedAt,
    string? SourceCommit, string? IntegratedCommit, string? Error, string? ConflictFiles);

/// <summary>Durable, per-project serialized integration queue for canonical ticket worktrees.</summary>
public sealed class WorktreeMergeQueueService(ProjectService projects, TicketWorktreeService worktrees)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static Task EnsureTableAsync(TodoDbContext db) => MigrationGate.RunOnceAsync(db, "worktree-merge-queue-v1", static d =>
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
                (TicketId, RootTicketId, WorktreePath, SourceBranch, TargetBranch, Status, CreatedAt, UpdatedAt)
            VALUES ({ticketId}, {worktree.RootTicketId}, {worktree.Path}, {worktree.Branch},
                {project.IntegrationBranch}, {(int)WorktreeMergeStatus.Pending}, {now}, {now})
            """);
        return (await ReadByRootAsync(db, worktree.RootTicketId))!;
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
            var request = await ReadNextAsync(db, WorktreeMergeStatus.Pending);
            if (request is null) return null;
            await UpdateAsync(db, request.Id, WorktreeMergeStatus.Processing);
            return await IntegrateAsync(projectSlug, request, continueRebase: false, ct);
        }
        finally { gate.Release(); }
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
            if (request is null || request.Status is not (WorktreeMergeStatus.Conflict or WorktreeMergeStatus.Failed))
                return null;
            await UpdateAsync(db, request.Id, WorktreeMergeStatus.Processing);
            return await IntegrateAsync(projectSlug, request, continueRebase: request.Status == WorktreeMergeStatus.Conflict, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<WorktreeMergeRequest> IntegrateAsync(string slug, WorktreeMergeRequest request, bool continueRebase, CancellationToken ct)
    {
        await using var db = projects.GetProjectDb(slug);
        await EnsureTableAsync(db);
        var project = await projects.GetProjectAsync(slug) ?? throw new InvalidOperationException("Project disappeared.");
        var repository = Path.GetFullPath(RunGit(projects.ResolveWorkspacePath(project), ["rev-parse", "--show-toplevel"]).Output.Trim());
        try
        {
            RequireClean(repository, "integration checkout");
            var checkedOut = RunGit(repository, ["branch", "--show-current"]).Output.Trim();
            if (!string.Equals(checkedOut, request.TargetBranch, StringComparison.Ordinal))
                throw new InvalidOperationException($"Integration checkout is on '{checkedOut}', expected '{request.TargetBranch}'.");

            var alreadyIntegrated = !string.IsNullOrWhiteSpace(request.SourceCommit)
                && RunGit(repository, ["merge-base", "--is-ancestor", request.SourceCommit!, request.TargetBranch], false).ExitCode == 0;
            if (!alreadyIntegrated)
            {
                if (continueRebase)
                {
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
                    RequireClean(request.WorktreePath, "ticket worktree");
                    var sourceCommit = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                    await SetSourceCommitAsync(db, request.Id, sourceCommit);
                    request = request with { SourceCommit = sourceCommit };
                    var rebased = RunGit(request.WorktreePath, ["rebase", request.TargetBranch], false);
                    if (rebased.ExitCode != 0)
                        return await MarkGitFailureAsync(db, request, rebased);
                }

                var rebasedCommit = RunGit(request.WorktreePath, ["rev-parse", "HEAD"]).Output.Trim();
                var ff = RunGit(repository, ["merge", "--ff-only", request.SourceBranch], false);
                if (ff.ExitCode != 0) throw new InvalidOperationException(ff.Error.Trim());
                if (RunGit(repository, ["merge-base", "--is-ancestor", rebasedCommit, request.TargetBranch], false).ExitCode != 0)
                    throw new InvalidOperationException($"Commit {rebasedCommit} is not reachable from {request.TargetBranch}.");
                await SetSourceCommitAsync(db, request.Id, rebasedCommit);
                request = request with { SourceCommit = rebasedCommit };
            }

            var integrated = request.SourceCommit ?? RunGit(repository, ["rev-parse", request.TargetBranch]).Output.Trim();
            RunGit(repository, ["worktree", "remove", request.WorktreePath]);
            RunGit(repository, ["branch", "-d", request.SourceBranch]);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE WorktreeMergeQueue SET Status = {(int)WorktreeMergeStatus.Completed},
                    IntegratedCommit = {integrated}, Error = NULL, ConflictFiles = NULL, UpdatedAt = {DateTime.UtcNow}
                WHERE Id = {request.Id}
                """);
            return (await ReadByIdAsync(db, request.Id))!;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return await MarkAsync(db, request.Id, WorktreeMergeStatus.Failed, ex.Message, null);
        }
    }

    private static void RequireClean(string path, string label)
    {
        var status = RunGit(path, ["status", "--porcelain"]).Output;
        if (!string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException($"The {label} '{path}' has uncommitted changes; nothing was modified.");
    }

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
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = {(int)status}, Error = {error}, ConflictFiles = {conflicts}, UpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");
        return (await ReadByIdAsync(db, id))!;
    }

    private static Task UpdateAsync(TodoDbContext db, long id, WorktreeMergeStatus status) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET Status = {(int)status}, UpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");

    private static Task SetSourceCommitAsync(TodoDbContext db, long id, string commit) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeMergeQueue SET SourceCommit = {commit}, UpdatedAt = {DateTime.UtcNow} WHERE Id = {id}");

    private static Task<WorktreeMergeRequest?> ReadNextAsync(TodoDbContext db, WorktreeMergeStatus status) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE Status = $status ORDER BY Id LIMIT 1", ("$status", (int)status));
    private static Task<WorktreeMergeRequest?> ReadByIdAsync(TodoDbContext db, long id) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE Id = $id", ("$id", id));
    private static Task<WorktreeMergeRequest?> ReadByRootAsync(TodoDbContext db, int root) =>
        ReadSingleAsync(db, "SELECT * FROM WorktreeMergeQueue WHERE RootTicketId = $root ORDER BY Id DESC LIMIT 1", ("$root", root));

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
            GetNullable(reader, "Error"), GetNullable(reader, "ConflictFiles")));
        return rows;
    }

    private static string? GetNullable(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
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

    private sealed record GitResult(int ExitCode, string Output, string Error);
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
