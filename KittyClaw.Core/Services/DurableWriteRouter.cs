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
            try
            {
                SynchronizeMaintenanceWorktree(
                    path, repository, project.IntegrationBranch!, normalized, allowWholeWorkspace);
            }
            catch (MaintenanceWorktreeNeedsQuarantineException ex) when (mergeQueue is not null)
            {
                await QuarantineMaintenanceWorktreeAsync(
                    projectSlug, repository, path, branch, project.IntegrationBranch!, safeSlug,
                    ex.Message, ct);
            }
            PrepareLocalDependencies(projects.ResolveWorkspacePath(project), path);
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

    private async Task QuarantineMaintenanceWorktreeAsync(
        string projectSlug,
        string repository,
        string path,
        string branch,
        string targetBranch,
        string safeSlug,
        string reason,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var quarantineBranch = $"recovery/maintenance-{safeSlug}-{suffix}";
        var quarantinePath = Path.Combine(
            Path.GetDirectoryName(path)!, $"maintenance-{safeSlug}-quarantine-{suffix}");
        var renamed = false;
        var moved = false;
        try
        {
            RunGit(path, ["branch", "-m", quarantineBranch]);
            renamed = true;
            RunGit(repository, ["worktree", "move", path, quarantinePath]);
            moved = true;
            await mergeQueue!.QuarantineMaintenanceAsync(
                projectSlug, path, quarantinePath, quarantineBranch, reason);
        }
        catch
        {
            if (moved)
                RunGit(repository, ["worktree", "move", quarantinePath, path], false);
            if (renamed && Directory.Exists(path))
                RunGit(path, ["branch", "-m", branch], false);
            throw;
        }

        RunGit(repository, ["worktree", "add", "-b", branch, path, targetBranch]);
        VerifyWorktree(path, branch);
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
            ProbableSecretScanner.ContainsProbableSecret(Path.Combine(route.RootPath, path))).ToArray();
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
    private static string SafeName(string value) => new(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
    private static void VerifyWorktree(string path, string branch)
    {
        var top = Path.GetFullPath(RunGit(path, ["rev-parse", "--show-toplevel"]).Output.Trim());
        if (!string.Equals(top, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !string.Equals(RunGit(path, ["branch", "--show-current"]).Output.Trim(), branch, StringComparison.Ordinal)) throw new InvalidOperationException($"Maintenance worktree '{path}' is not on '{branch}'.");
    }
    private static void SynchronizeMaintenanceWorktree(
        string path,
        string repository,
        string targetBranch,
        IReadOnlyList<string> allowedPaths,
        bool allowWholeWorkspace)
    {
        var status = RunGit(path, ["status", "--porcelain", "--untracked-files=all"], false);
        if (status.ExitCode != 0)
            throw new InvalidOperationException($"Maintenance worktree '{path}' could not be inspected.");
        if (!string.IsNullOrWhiteSpace(status.Output))
        {
            var changed = StatusPaths(path);
            var unexpected = changed.Where(candidate => IsLocalOnly(candidate) ||
                (!allowWholeWorkspace && !IsAllowed(candidate, allowedPaths))).ToArray();
            if (unexpected.Length > 0)
                throw new MaintenanceWorktreeNeedsQuarantineException(
                    $"Maintenance worktree '{path}' contains changes that require review: {string.Join(", ", unexpected)}.");

            var secrets = changed.Where(candidate =>
                (allowWholeWorkspace || IsAllowed(candidate, allowedPaths)) &&
                ProbableSecretScanner.ContainsProbableSecret(Path.Combine(path, candidate))).ToArray();
            if (secrets.Length > 0)
                throw new MaintenanceWorktreeNeedsQuarantineException(
                    $"Maintenance worktree '{path}' contains possible secrets that require review: {string.Join(", ", secrets)}.");

            // A previous durable writer may have stopped after changing safe project files but
            // before its final commit. Keep those files in place so the next compatible writer
            // can validate, commit and enqueue them together with its own changes.
            return;
        }

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

        // A clean divergent branch contains committed durable writes that have not reached the
        // target yet. Reuse it and let the merge queue perform the normal conflict-safe merge.
        // Even a no-op writer will enqueue it through MarkMaintenanceNoChangesAsync.
    }
    private static void PrepareLocalDependencies(string canonicalWorkspace, string worktreePath)
    {
        canonicalWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalWorkspace));
        worktreePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
        if (string.Equals(canonicalWorkspace, worktreePath, StringComparison.OrdinalIgnoreCase))
            return;

        var worktreeContainer = Directory.GetParent(worktreePath)?.FullName;
        foreach (var source in FindLocalDependencyDirectories(canonicalWorkspace, worktreeContainer))
        {
            var relative = Path.GetRelativePath(canonicalWorkspace, source);
            var target = Path.Combine(worktreePath, relative);
            if (Directory.Exists(target)) continue;
            if (File.Exists(target))
                throw new InvalidOperationException(
                    $"Local dependency preparation failed: '{target}' exists and is not a directory.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CreateDirectoryLink(target, source);
                if (!Directory.Exists(target))
                    throw new IOException("The created directory link cannot be resolved.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Local dependency preparation failed: could not expose '{source}' as '{target}'.", ex);
            }
        }
    }

    private static void CreateDirectoryLink(string target, string source)
    {
        try
        {
            Directory.CreateSymbolicLink(target, source);
            return;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", target, source })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)
                ?? throw new IOException("The Windows junction helper could not be started.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(true);
                throw new IOException("The Windows junction helper timed out.");
            }
            if (process.ExitCode != 0)
                throw new IOException($"The Windows junction helper failed: {(error.Length > 0 ? error : output).Trim()}");
        }
    }

    private static IEnumerable<string> FindLocalDependencyDirectories(string root, string? excludedRoot)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        excludedRoot = string.IsNullOrWhiteSpace(excludedRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedRoot));
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Local dependency preparation failed while inspecting '{current}'.", ex);
            }

            foreach (var child in children)
            {
                if (IsSameOrDescendant(child, excludedRoot)) continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                var name = Path.GetFileName(child);
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                    continue;
                }
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
    }

    private static bool IsSameOrDescendant(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var ancestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
    private sealed class MaintenanceWorktreeNeedsQuarantineException(string message)
        : InvalidOperationException(message);
    private sealed record GitResult(int ExitCode, string Output, string Error);
}
