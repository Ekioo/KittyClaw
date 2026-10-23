using System.Collections.Concurrent;
using System.Diagnostics;

namespace KittyClaw.Core.Services;

/// <summary>Checkpoints only the two native processor-memory files, preserving unrelated index entries.</summary>
internal static class ProcessorMemoryGit
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task CommitAsync(string workspace, int columnId, CancellationToken ct = default)
    {
        if (!Directory.Exists(workspace)) return;
        var memory = Path.Combine(workspace, ".agents", "processors", $"column-{columnId}", "memory");
        var repository = await GitAsync(workspace, ["rev-parse", "--show-toplevel"], ct);
        if (repository.Code != 0) return; // Non-Git workspaces have no commit boundary.
        var root = repository.Output.Trim();
        var relative = Path.GetRelativePath(root, memory).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidOperationException("Processor memory is outside its Git repository.");
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var paths = new List<string>();
            foreach (var name in new[] { "MEMORY.md", "pipeline-lessons.md" })
            {
                var path = relative + "/" + name;
                var fullPath = Path.Combine(memory, name);
                var tracked = await GitAsync(root, ["ls-files", "--", path], ct);
                if (!File.Exists(fullPath) && string.IsNullOrWhiteSpace(tracked.Output)) continue;
                if (ProbableSecretScanner.ContainsProbableSecret(fullPath))
                    throw new InvalidOperationException($"Processor memory requires review before commit: {path}");
                paths.Add(path);
            }
            if (paths.Count == 0) return;
            // These generated knowledge files are versioned even when a project ignores .agents.
            var add = await GitAsync(root, ["add", "-f", "-A", "--", .. paths], ct);
            EnsureSuccess(add);
            var diff = await GitAsync(root, ["diff", "--cached", "--quiet", "--", .. paths], ct);
            if (diff.Code == 0) return;
            if (diff.Code != 1) EnsureSuccess(diff);
            var identity = $"column-{columnId}";
            EnsureSuccess(await GitAsync(root,
                ["-c", $"user.name={identity}", "-c", $"user.email={identity}@kittyclaw.local",
                    "commit", "--only", "--no-verify", "-m", $"chore(memory): checkpoint column {columnId}", "--", .. paths], ct));
        }
        finally { gate.Release(); }
    }

    private static void EnsureSuccess((int Code, string Output, string Error) result)
    {
        if (result.Code != 0)
            throw new InvalidOperationException($"Processor memory commit failed: {result.Error.Trim()}");
    }

    private static async Task<(int Code, string Output, string Error)> GitAsync(
        string cwd, IReadOnlyList<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git for processor memory.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}
