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

            ctx.Sessions.SetLastProcessedCommit(ctx.WorkspacePath, currentHead);
            IReadOnlyList<TriggerFiring> fire = new[] { new TriggerFiring(null, $"commit {currentHead[..7]}", null) };
            return Task.FromResult(fire);
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
