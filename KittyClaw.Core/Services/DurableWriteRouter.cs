using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Services;

public enum DurableWriteKind { Ticket, Maintenance }
public enum DurableWriteValidationStatus { Ready, NeedsReview, SecretBlocked }
public sealed record DurableWriteRoute(string RootPath, string Branch, DurableWriteKind Kind, int? RootTicketId, IReadOnlyList<string> AllowedPaths, long? QueueRequestId = null)
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
    {
        var project = await projects.GetProjectAsync(projectSlug) ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        var normalized = NormalizeAllowedPaths(allowedPaths);
        if (!project.WorktreesEnabled)
            return new(projects.ResolveWorkspacePath(project), project.IntegrationBranch ?? "", ticketId.HasValue ? DurableWriteKind.Ticket : DurableWriteKind.Maintenance, ticketId, normalized);
        if (ticketId is int id)
        {
            var worktree = await ticketWorktrees.ResolveAsync(projectSlug, id, ct) ?? throw new InvalidOperationException("Ticket worktree resolution returned no worktree.");
            return new(worktree.Path, worktree.Branch, DurableWriteKind.Ticket, worktree.RootTicketId, normalized);
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
        }
        catch
        {
            gate.Release();
            throw;
        }
        try
        {
            var request = mergeQueue is null ? null : await mergeQueue.PrepareMaintenanceAsync(projectSlug, path, branch, ct);
            var route = new DurableWriteRoute(path, branch, DurableWriteKind.Maintenance, null, normalized, request?.Id);
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
        var unexpected = changed.Where(path => !IsAllowed(path, route.AllowedPaths)).ToArray();
        if (unexpected.Length > 0) return Task.FromResult(new DurableWriteValidationResult(DurableWriteValidationStatus.NeedsReview, unexpected, []));
        var secrets = changed.Where(path => IsAllowed(path, route.AllowedPaths) && ContainsProbableSecret(Path.Combine(route.RootPath, path))).ToArray();
        if (secrets.Length > 0) return Task.FromResult(new DurableWriteValidationResult(DurableWriteValidationStatus.SecretBlocked, [], secrets));
        foreach (var allowed in route.AllowedPaths)
        {
            var add = RunGit(route.RootPath, ["add", "--", allowed], false);
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
            if (staged)
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

    private static IReadOnlyList<string> NormalizeAllowedPaths(IEnumerable<string> paths)
    {
        var result = paths.Select(path => path.Replace('\\', '/').Trim('/')).Where(path => path.Length > 0 && !Path.IsPathRooted(path) && path.Split('/').All(p => p is not ".." and not ".")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0) throw new ArgumentException("At least one safe relative path must be declared.", nameof(paths));
        if (result.Any(path => LocalOnlyRegex().IsMatch('/' + path + '/'))) throw new InvalidOperationException("Transcripts, prompts, sessions, traces and secrets are local-only.");
        return result;
    }
    private static bool IsAllowed(string path, IReadOnlyList<string> allowed) => allowed.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase) || path.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase));
    private static string[] StatusPaths(string root) => RunGit(root, ["status", "--porcelain", "-z", "--untracked-files=all"]).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Length > 3 ? entry[3..].Replace('\\', '/') : "").Where(path => path.Length > 0).ToArray();
    private static bool ContainsProbableSecret(string path) => File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 && SecretRegex().IsMatch(File.ReadAllText(path));
    private static string SafeName(string value) => new(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
    private static void VerifyWorktree(string path, string branch)
    {
        var top = Path.GetFullPath(RunGit(path, ["rev-parse", "--show-toplevel"]).Output.Trim());
        if (!string.Equals(top, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !string.Equals(RunGit(path, ["branch", "--show-current"]).Output.Trim(), branch, StringComparison.Ordinal)) throw new InvalidOperationException($"Maintenance worktree '{path}' is not on '{branch}'.");
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
    [GeneratedRegex(@"/(transcripts?|prompts?|sessions?|traces?|secrets?)/|(^|/)\.env(/|$)", RegexOptions.IgnoreCase)] private static partial Regex LocalOnlyRegex();
    [GeneratedRegex("(?i)(api[_-]?key|access[_-]?token|client[_-]?secret|password|private[_-]?key)\\s*[:=]\\s*['\\\"]?[A-Za-z0-9_\\-/+=]{8,}")] private static partial Regex SecretRegex();
    private sealed record GitResult(int ExitCode, string Output, string Error);
}
