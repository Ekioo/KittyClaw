using System.Diagnostics;
using KittyClaw.Core.Automation;

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
            using var job = ProcessJobObject.TryCreateAndAssign(proc);
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
        ct.ThrowIfCancellationRequested();

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

        return await RunAsync(psi, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an argument-list invocation, optionally copying raw stdout bytes to a caller-owned
    /// stream. The deadline covers process exit and pipe completion; transfer errors fail closed.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo, TimeSpan? timeout = null,
        CancellationToken ct = default, Stream? standardOutput = null)
    {
        ct.ThrowIfCancellationRequested();
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        using var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}' process");
        using var job = ProcessJobObject.TryCreateAndAssign(proc);

        var stdoutTask = CaptureOutputAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            // A descendant can retain inherited pipes after the parent exits. Close the job
            // first, and keep the original deadline active while awaiting the final drain.
            var exitCode = proc.ExitCode;
            job?.Dispose();
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linked.Token).ConfigureAwait(false);
            return new ProcessResult(exitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            Stop();
            await DrainPumpsBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(null,
                stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty,
                stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty,
                TimedOut: true);
        }
        catch
        {
            Stop();
            await DrainPumpsBoundedAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        async Task<string> CaptureOutputAsync()
        {
            if (standardOutput is null)
                return await proc.StandardOutput.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            await proc.StandardOutput.BaseStream.CopyToAsync(standardOutput, linked.Token).ConfigureAwait(false);
            return string.Empty;
        }

        void Stop()
        {
            linked.Cancel();
            job?.Dispose();
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* may have exited between the check and the kill */ }
        }
    }

    private static async Task DrainPumpsBoundedAsync(params Task<string>[] pumps)
    {
        try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { }
    }
}
