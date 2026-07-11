using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation.Triggers;

/// <summary>Signal emitted by GitRepositoryWatcher when new commits are detected via FileSystemWatcher.</summary>
public sealed record GitCommitSignal(string Slug);

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

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Array.Empty<TriggerFiring>();
        _lastPolled = ctx.Now;

        try
        {
            if (!Directory.Exists(Path.Combine(ctx.WorkspacePath, ".git")))
                return Array.Empty<TriggerFiring>();

            var currentHead = (await RunGitAsync(ctx.WorkspacePath, "rev-parse HEAD", ct))?.Trim();
            if (string.IsNullOrEmpty(currentHead))
                return Array.Empty<TriggerFiring>();

            var last = ctx.Sessions.LastProcessedCommit(ctx.WorkspacePath);
            if (last == currentHead)
                return Array.Empty<TriggerFiring>();

            // Enumerate new commits between last processed and HEAD
            var firings = new List<TriggerFiring>();
            var range = string.IsNullOrEmpty(last) ? currentHead : $"{last}..{currentHead}";
            var logOutput = (await RunGitAsync(ctx.WorkspacePath, $"log --format=%H|%ae {range}", ct))?.Trim();

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
            return firings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<TriggerFiring>();
        }
    }

    public bool TryHandleExternalSignal(object signal, out IReadOnlyList<TriggerFiring> firings)
    {
        if (signal is not GitCommitSignal)
        {
            firings = Array.Empty<TriggerFiring>();
            return false;
        }
        // Reset the poll debounce so EvaluateAsync runs immediately on the next engine tick.
        _lastPolled = DateTime.MinValue;
        firings = Array.Empty<TriggerFiring>();
        // Return false: the actual commit enumeration happens in EvaluateAsync which will
        // run immediately (debounce reset) and produce the real per-commit firings.
        return false;
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);

    private static async Task<string?> RunGitAsync(string cwd, string args, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(15), ct: ct);
            return res.Success ? res.Stdout : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
