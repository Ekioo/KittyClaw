using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Services;

public enum DurableWriteKind { Ticket, Maintenance }
public enum DurableWriteValidationStatus { Ready, NeedsReview, SecretBlocked }
public sealed record DurableWriteRoute(string RootPath, string Branch, DurableWriteKind Kind, int? RootTicketId, IReadOnlyList<string> AllowedPaths, long? QueueRequestId = null, bool AllowWholeWorkspace = false)
{
    internal void AttachMaintenanceLease(SemaphoreSlim lease) => _maintenanceLease = lease;
    internal void ReleaseMaintenanceLease() => Interlocked.Exchange(ref _maintenanceLease, null)?.Release();

    private SemaphoreSlim? _maintenanceLease;
}
public sealed record DurableWriteValidationResult(DurableWriteValidationStatus Status, IReadOnlyList<string> UnexpectedPaths, IReadOnlyList<string> SecretPaths, string? Error = null);

/// <summary>Routes versioned durable writes to isolated worktrees and stages only declared paths.</summary>
public sealed partial class DurableWriteRouter(ProjectService projects, TicketWorktreeService ticketWorktrees, WorktreeMergeQueueService? mergeQueue = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MaintenanceGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DurableWriteRoute> ResolveAsync(string projectSlug, int? ticketId, IEnumerable<string> allowedPaths, CancellationToken ct = default)
        => await ResolveCoreAsync(projectSlug, ticketId, NormalizeAllowedPaths(allowedPaths), allowWholeWorkspace: false, ct);

    /// <summary>
    /// Routes an interactive project-wide instruction to an isolated worktree. Unlike the
    /// narrow durable-write routes used by dashboards and memories, the owner can legitimately
    /// ask a chat agent to edit any versioned project file. Local-only control data and probable
    /// secrets remain blocked during validation.
    /// </summary>
    public async Task<DurableWriteRoute?> TryResolveWorkspaceAsync(
        string projectSlug, int? ticketId = null, CancellationToken ct = default)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        if (!project.WorktreesEnabled) return null;
        return await ResolveCoreAsync(projectSlug, ticketId, [], allowWholeWorkspace: true, ct);
    }

    private async Task<DurableWriteRoute> ResolveCoreAsync(
        string projectSlug,
        int? ticketId,
        IReadOnlyList<string> normalized,
        bool allowWholeWorkspace,
        CancellationToken ct)
    {
        var project = await projects.GetProjectAsync(projectSlug) ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        if (!project.WorktreesEnabled)
            return new(projects.ResolveWorkspacePath(project), project.IntegrationBranch ?? "", ticketId.HasValue ? DurableWriteKind.Ticket : DurableWriteKind.Maintenance, ticketId, normalized, AllowWholeWorkspace: allowWholeWorkspace);
        if (ticketId is int id)
        {
            var worktree = await ticketWorktrees.ResolveAsync(projectSlug, id, ct) ?? throw new InvalidOperationException("Ticket worktree resolution returned no worktree.");
            return new(worktree.Path, worktree.Branch, DurableWriteKind.Ticket, worktree.RootTicketId, normalized, AllowWholeWorkspace: allowWholeWorkspace);
        }

        var repository = projects.ResolveRepositoryPath(project);
        var safeSlug = SafeName(projectSlug);
        var branch = $"maintenance/{safeSlug}";
        var parent = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException($"Repository '{repository}' has no parent directory.");
        var repositoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(repository));
        var path = Path.GetFullPath(Path.Combine(parent, $"{repositoryName}.worktrees", $"maintenance-{safeSlug}"));
        var gate = MaintenanceGates.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var listed = RunGit(repository, ["worktree", "list", "--porcelain"]).Output.Replace('\\', '/');
            if (!listed.Contains($"worktree {path.Replace('\\', '/')}", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var exists = RunGit(repository, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], false).ExitCode == 0;
                RunGit(repository, exists ? ["worktree", "add", path, branch] : ["worktree", "add", "-b", branch, path, project.IntegrationBranch!]);
            }
            VerifyWorktree(path, branch);
            SynchronizeMaintenanceWorktree(path, repository, project.IntegrationBranch!);
        }
        catch
        {
            gate.Release();
            throw;
        }
        try
        {
            var request = mergeQueue is null ? null : await mergeQueue.PrepareMaintenanceAsync(projectSlug, path, branch, ct);
            var route = new DurableWriteRoute(path, branch, DurableWriteKind.Maintenance, null, normalized, request?.Id, allowWholeWorkspace);
            route.AttachMaintenanceLease(gate);
            return route;
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public Task<DurableWriteValidationResult> ValidateAndStageAsync(DurableWriteRoute route, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var changed = StatusPaths(route.RootPath);
        var unexpected = changed.Where(path => IsLocalOnly(path) ||
            (!route.AllowWholeWorkspace && !IsAllowed(path, route.AllowedPaths))).ToArray();
        if (unexpected.Length > 0) return Task.FromResult(new DurableWriteValidationResult(DurableWriteValidationStatus.NeedsReview, unexpected, []));
        var secrets = changed.Where(path =>
            (route.AllowWholeWorkspace || IsAllowed(path, route.AllowedPaths)) &&
            ContainsProbableSecret(Path.Combine(route.RootPath, path))).ToArray();
        if (secrets.Length > 0) return Task.FromResult(new DurableWriteValidationResult(DurableWriteValidationStatus.SecretBlocked, [], secrets));
        if (route.AllowWholeWorkspace)
        {
            foreach (var path in changed.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var add = RunGit(route.RootPath, ["add", "-A", "--", path], false);
                if (add.ExitCode != 0)
                    return Task.FromResult(new DurableWriteValidationResult(
                        DurableWriteValidationStatus.NeedsReview, [], [], add.Error.Trim()));
            }
            return Task.FromResult(new DurableWriteValidationResult(DurableWriteValidationStatus.Ready, [], []));
        }
        foreach (var allowed in route.AllowedPaths)
        {
            var exists = File.Exists(Path.Combine(route.RootPath, allowed))
                || Directory.Exists(Path.Combine(route.RootPath, allowed));
            var tracked = !string.IsNullOrWhiteSpace(
                RunGit(route.RootPath, ["ls-files", "--", allowed], false).Output);
            if (!exists && !tracked) continue;
            var add = RunGit(route.RootPath, ["add", "-A", "--", allowed], false);
            if (add.ExitCode != 0) return Task.FromResult(new DurableWriteValidationResult(DurableWriteValidationStatus.NeedsReview, [], [], add.Error.Trim()));
        }
        return Task.FromResult(new DurableWriteValidationResult(DurableWriteValidationStatus.Ready, [], []));
    }

    public async Task<DurableWriteValidationResult> CommitAndQueueAsync(
        string projectSlug, DurableWriteRoute route, string message, CancellationToken ct = default)
    {
        try
        {
            var validation = await ValidateAndStageAsync(route, ct);
            if (route.Kind != DurableWriteKind.Maintenance || route.QueueRequestId is not long requestId || mergeQueue is null)
                return validation;
            if (validation.Status != DurableWriteValidationStatus.Ready)
            {
                await mergeQueue.MarkReviewRequiredAsync(projectSlug, requestId,
                    validation.Error ?? (validation.SecretPaths.Count > 0
                        ? $"Possible secret detected in: {string.Join(", ", validation.SecretPaths)}"
                        : $"Unexpected files detected in: {string.Join(", ", validation.UnexpectedPaths)}"));
                return validation;
            }

            var staged = RunGit(route.RootPath, ["diff", "--cached", "--quiet"], false).ExitCode != 0;
            if (!staged)
            {
                var unchanged = RunGit(route.RootPath, ["rev-parse", "HEAD"]).Output.Trim();
                await mergeQueue.MarkMaintenanceNoChangesAsync(projectSlug, requestId, unchanged);
                return validation;
            }
            RunGit(route.RootPath, ["commit", "-m", message]);
            var commit = RunGit(route.RootPath, ["rev-parse", "HEAD"]).Output.Trim();
            await mergeQueue.MarkMaintenanceReadyAsync(projectSlug, requestId, commit);
            return validation;
        }
        finally
        {
            if (route.QueueRequestId is long requestId)
                mergeQueue?.ReleaseMaintenanceWrite(requestId);
            route.ReleaseMaintenanceLease();
        }
    }

    public async Task PreserveExecutionAsync(string projectSlug, DurableWriteRoute route, string reason)
    {
        try
        {
            if (route.QueueRequestId is long requestId && mergeQueue is not null)
                await mergeQueue.MarkReviewRequiredAsync(projectSlug, requestId, reason);
        }
        finally
        {
            if (route.QueueRequestId is long requestId)
                mergeQueue?.ReleaseMaintenanceWrite(requestId);
            route.ReleaseMaintenanceLease();
        }
    }

    public async Task CloseOrPreserveExecutionAsync(
        string projectSlug, DurableWriteRoute route, string reason, CancellationToken ct = default)
    {
        try
        {
            if (route.Kind != DurableWriteKind.Maintenance ||
                route.QueueRequestId is not long requestId || mergeQueue is null)
                return;

            var validation = await ValidateAndStageAsync(route, ct);
            var staged = validation.Status == DurableWriteValidationStatus.Ready &&
                RunGit(route.RootPath, ["diff", "--cached", "--quiet"], false).ExitCode != 0;
            if (validation.Status == DurableWriteValidationStatus.Ready && !staged)
            {
                var head = RunGit(route.RootPath, ["rev-parse", "HEAD"]).Output.Trim();
                await mergeQueue.MarkMaintenanceNoChangesAsync(projectSlug, requestId, head);
                return;
            }

            await mergeQueue.MarkReviewRequiredAsync(projectSlug, requestId, reason);
        }
        finally
        {
            if (route.QueueRequestId is long requestId)
                mergeQueue?.ReleaseMaintenanceWrite(requestId);
            route.ReleaseMaintenanceLease();
        }
    }

    private static IReadOnlyList<string> NormalizeAllowedPaths(IEnumerable<string> paths)
    {
        var result = paths.Select(path => path.Replace('\\', '/').Trim('/')).Where(path => path.Length > 0 && !Path.IsPathRooted(path) && path.Split('/').All(p => p is not ".." and not ".")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0) throw new ArgumentException("At least one safe relative path must be declared.", nameof(paths));
        if (result.Any(path => LocalOnlyRegex().IsMatch('/' + path + '/'))) throw new InvalidOperationException("Transcripts, prompts, sessions, traces and secrets are local-only.");
        return result;
    }
    private static bool IsAllowed(string path, IReadOnlyList<string> allowed) => allowed.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase) || path.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase));
    private static string[] StatusPaths(string root)
    {
        var entries = RunGit(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"])
            .Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4) continue;
            var status = entry[..2];
            paths.Add(entry[3..].Replace('\\', '/'));
            if ((status.Contains('R') || status.Contains('C')) && index + 1 < entries.Length)
                paths.Add(entries[++index].Replace('\\', '/'));
        }
        return paths.Where(path => path.Length > 0).ToArray();
    }
    private static bool IsLocalOnly(string path) => LocalOnlyRegex().IsMatch('/' + path.Replace('\\', '/') + '/');
    private static bool ContainsProbableSecret(string path) => File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 && SecretRegex().IsMatch(File.ReadAllText(path));
    private static string SafeName(string value) => new(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
    private static void VerifyWorktree(string path, string branch)
    {
        var top = Path.GetFullPath(RunGit(path, ["rev-parse", "--show-toplevel"]).Output.Trim());
        if (!string.Equals(top, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !string.Equals(RunGit(path, ["branch", "--show-current"]).Output.Trim(), branch, StringComparison.Ordinal)) throw new InvalidOperationException($"Maintenance worktree '{path}' is not on '{branch}'.");
    }
    private static void SynchronizeMaintenanceWorktree(string path, string repository, string targetBranch)
    {
        var status = RunGit(path, ["status", "--porcelain", "--untracked-files=all"], false);
        if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.Output))
            throw new InvalidOperationException(
                $"Maintenance worktree '{path}' contains uncommitted changes and must be recovered before another durable write.");

        var head = RunGit(path, ["rev-parse", "HEAD"]).Output.Trim();
        var target = RunGit(repository, ["rev-parse", targetBranch]).Output.Trim();
        if (string.Equals(head, target, StringComparison.Ordinal)) return;

        if (RunGit(repository, ["merge-base", "--is-ancestor", head, target], false).ExitCode == 0)
        {
            RunGit(path, ["merge", "--ff-only", target]);
            return;
        }

        // A clean branch ahead of the target contains a previous durable write that has not
        // reached the queue yet. Keep using that history: PrepareMaintenanceAsync plus the
        // no-op checkpoint will recover and enqueue the complete branch atomically.
        if (RunGit(repository, ["merge-base", "--is-ancestor", target, head], false).ExitCode == 0)
            return;

        throw new InvalidOperationException(
            $"Maintenance worktree '{path}' has diverged from '{targetBranch}' and requires recovery.");
    }
    private static GitResult RunGit(string cwd, IReadOnlyList<string> args, bool throwOnError = true)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git could not be started.");
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new InvalidOperationException("Git command timed out."); }
        var result = new GitResult(process.ExitCode, output, error);
        if (throwOnError && result.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        return result;
    }
    [GeneratedRegex(@"/(channel|transcripts?|prompts?|sessions?|traces?|secrets?)/|(^|/)\.env(/|$)", RegexOptions.IgnoreCase)] private static partial Regex LocalOnlyRegex();
    [GeneratedRegex("(?i)(api[_-]?key|access[_-]?token|client[_-]?secret|password|private[_-]?key)\\s*[:=]\\s*['\\\"]?[A-Za-z0-9_\\-/+=]{8,}")] private static partial Regex SecretRegex();
    private sealed record GitResult(int ExitCode, string Output, string Error);
}
