using System.Collections.Concurrent;
using System.Diagnostics;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed record TicketWorktree(string Path, string Branch, int RootTicketId);

/// <summary>Resolves and creates the canonical Git worktree used by a ticket family.</summary>
public sealed class TicketWorktreeService(ProjectService projects, TicketService tickets)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CreationGates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<TicketWorktree?> ResolveAsync(string projectSlug, int ticketId, CancellationToken ct)
    {
        var project = await projects.GetProjectAsync(projectSlug)
            ?? throw new InvalidOperationException($"Project '{projectSlug}' does not exist.");
        if (!project.WorktreesEnabled)
            return null;

        var workspace = projects.ResolveWorkspacePath(project);
        var rootTicketId = await ResolveRootTicketIdAsync(projectSlug, ticketId);
        var repositoryRoot = RunGit(workspace, ["rev-parse", "--show-toplevel"]).Output.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new InvalidOperationException($"Git could not resolve the repository containing '{workspace}'.");

        repositoryRoot = Path.GetFullPath(repositoryRoot);
        var branch = $"ticket/{rootTicketId}";
        var repositoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(repositoryRoot));
        var parent = Directory.GetParent(repositoryRoot)?.FullName
            ?? throw new InvalidOperationException($"Git repository '{repositoryRoot}' has no parent directory.");
        var path = Path.GetFullPath(Path.Combine(parent, $"{repositoryName}.worktrees", $"ticket-{rootTicketId}"));
        var key = $"{repositoryRoot}|{rootTicketId}";
        var gate = CreationGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct);
        try
        {
            var existing = FindRegisteredWorktree(repositoryRoot, path);
            if (existing is not null)
            {
                if (!string.Equals(existing, $"refs/heads/{branch}", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Worktree '{path}' is registered on '{existing}', expected 'refs/heads/{branch}'.");
                VerifyWorktree(path, branch);
                return new TicketWorktree(path, branch, rootTicketId);
            }

            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                throw new InvalidOperationException($"Canonical worktree path '{path}' already exists but is not registered with Git.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var branchExists = RunGit(repositoryRoot, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], throwOnError: false).ExitCode == 0;
            if (branchExists)
                RunGit(repositoryRoot, ["worktree", "add", path, branch]);
            else
                RunGit(repositoryRoot, ["worktree", "add", "-b", branch, path, project.IntegrationBranch!]);

            VerifyWorktree(path, branch);
            return new TicketWorktree(path, branch, rootTicketId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> ResolveRootTicketIdAsync(string projectSlug, int ticketId)
    {
        var visited = new HashSet<int>();
        var currentId = ticketId;
        while (true)
        {
            if (!visited.Add(currentId))
                throw new InvalidOperationException($"Ticket parent cycle detected at ticket #{currentId}.");
            var ticket = await tickets.GetTicketAsync(projectSlug, currentId)
                ?? throw new InvalidOperationException($"Ticket #{currentId} does not exist in project '{projectSlug}'.");
            if (ticket.ParentId is not int parentId)
                return currentId;
            currentId = parentId;
        }
    }

    private static string? FindRegisteredWorktree(string repositoryRoot, string desiredPath)
    {
        var output = RunGit(repositoryRoot, ["worktree", "list", "--porcelain"]).Output;
        string? currentPath = null;
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                currentPath = Path.GetFullPath(line[9..]);
            else if (currentPath is not null && line.StartsWith("branch ", StringComparison.Ordinal)
                && string.Equals(currentPath, desiredPath, StringComparison.OrdinalIgnoreCase))
                return line[7..];
            else if (line.Length == 0)
                currentPath = null;
        }
        return null;
    }

    private static void VerifyWorktree(string path, string branch)
    {
        var topLevel = Path.GetFullPath(RunGit(path, ["rev-parse", "--show-toplevel"]).Output.Trim());
        if (!string.Equals(topLevel, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Git resolved '{path}' to unexpected worktree '{topLevel}'.");
        var actualBranch = RunGit(path, ["branch", "--show-current"]).Output.Trim();
        if (!string.Equals(actualBranch, branch, StringComparison.Ordinal))
            throw new InvalidOperationException($"Worktree '{path}' uses branch '{actualBranch}', expected '{branch}'.");
    }

    private static GitResult RunGit(string workingDirectory, IReadOnlyList<string> arguments, bool throwOnError = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Git process could not be started.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"Git command timed out: git {string.Join(' ', arguments)}");
            }
            var result = new GitResult(process.ExitCode, stdout, stderr);
            if (throwOnError && result.ExitCode != 0)
                throw new InvalidOperationException($"Git command failed ({result.ExitCode}): git {string.Join(' ', arguments)}\n{stderr.Trim()}");
            return result;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Git command failed to start in '{workingDirectory}': {ex.Message}", ex);
        }
    }


    private sealed record GitResult(int ExitCode, string Output, string Error);
}
