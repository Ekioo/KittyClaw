using System.Diagnostics;

namespace KittyClaw.Core.Services;

/// <summary>
/// Hardened one-shot subprocess execution shared by the non-agent process call sites
/// (git helpers, executePowerShell, template init checks). Guarantees: concurrent
/// stdout/stderr drain (a sequential ReadToEnd deadlocks once the child fills the other
/// pipe's buffer), a wall-clock timeout, and best-effort kill of the whole process tree
/// on timeout or cancellation so no zombie keeps running after we stop waiting.
/// Agent subprocesses have their own lifecycle (AgentRunner) and do not use this.
/// </summary>
public static class ProcessRunner
{
    public sealed record ProcessResult(int? ExitCode, string Stdout, string Stderr, bool TimedOut)
    {
        public bool Success => !TimedOut && ExitCode == 0;
    }

    /// <summary>
    /// Synchronous availability probe for use from <c>Lazy&lt;T&gt;</c> initializers that
    /// cannot be async. Uses event-based pipe draining and <see cref="Process.WaitForExit(int)"/>
    /// — no async machinery — so this is genuine blocking I/O, not sync-over-async.
    /// Returns <c>true</c> when the process exits with code 0 within <paramref name="timeout"/>.
    /// </summary>
    public static bool ProbeSync(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            // Drain pipes via internal thread pool to prevent buffer-fill deadlock.
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            bool exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            proc.WaitForExit(); // flush async stream handlers before reading ExitCode
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Runs the process to completion. Returns a TimedOut result (process tree killed) when
    /// <paramref name="timeout"/> elapses; throws OperationCanceledException (process tree
    /// killed) when <paramref name="ct"/> fires.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}' process");

        // Killing the process (below) closes its pipe ends, so these reads always complete.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* may have exited between the check and the kill */ }
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(null,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false),
                TimedOut: true);
        }

        return new ProcessResult(proc.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            TimedOut: false);
    }
}
