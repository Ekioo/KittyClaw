using System.Diagnostics;

namespace Todo.Core.Automation.Triggers;

/// <summary>
/// Fires once per new git commit observed since last evaluation.
/// Uses SessionRegistry's _lastProcessedCommit to persist state across restarts,
/// preserving compatibility with existing dispatch-state.json files.
/// </summary>
public sealed class GitCommitTrigger : ITrigger
{
    private DateTime _lastPolled = DateTime.MinValue;
    private readonly GitCommitTriggerSpec _spec;

    public GitCommitTrigger(GitCommitTriggerSpec spec) { _spec = spec; }

    public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());
        _lastPolled = ctx.Now;

        try
        {
            if (!Directory.Exists(Path.Combine(ctx.WorkspacePath, ".git")))
                return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());

            var currentHead = RunGit(ctx.WorkspacePath, "rev-parse HEAD")?.Trim();
            if (string.IsNullOrEmpty(currentHead))
                return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());

            var last = ctx.Sessions.LastProcessedCommit(ctx.WorkspacePath);
            if (last == currentHead)
                return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());

            // Enumerate new commits between last processed and HEAD
            var firings = new List<TriggerFiring>();
            var range = string.IsNullOrEmpty(last) ? currentHead : $"{last}..{currentHead}";
            var logOutput = RunGit(ctx.WorkspacePath, $"log --format=%H|%ae {range}")?.Trim();

            if (!string.IsNullOrEmpty(logOutput))
            {
                var ignoreSet = new HashSet<string>(_spec.IgnoreAuthors, StringComparer.OrdinalIgnoreCase);
                foreach (var line in logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('|', 2);
                    if (parts.Length < 2) continue;
                    var hash = parts[0].Trim();
                    var email = parts[1].Trim();
                    if (!ignoreSet.Contains(email))
                        firings.Add(new TriggerFiring(null, $"commit {hash[..7]}", null));
                }
            }

            // Always advance the pointer, even for ignored commits
            ctx.Sessions.SetLastProcessedCommit(ctx.WorkspacePath, currentHead);
            return Task.FromResult<IReadOnlyList<TriggerFiring>>(firings);
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());
        }
    }

    private static string? RunGit(string cwd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
