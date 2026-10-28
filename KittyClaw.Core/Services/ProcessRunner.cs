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
    /// Synchronous availability probe for Lazy initializers. Both streams are discarded as
    /// bytes without accumulating lines. The shared runner bounds process exit and EOF waits
    /// and never captures the caller's synchronization context, so blocking here is safe.
    /// </summary>
    public static bool ProbeSync(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            return RunAsync(new ProcessStartInfo(fileName, arguments), timeout,
                standardOutput: Stream.Null, standardError: Stream.Null).GetAwaiter().GetResult().Success;
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
    /// Runs an argument-list invocation, optionally copying raw output bytes to caller-owned
    /// streams (Stream.Null discards output). The deadline covers process exit and both pipe
    /// transfers; transfer errors fail closed. Internal awaits do not capture the caller's context.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo, TimeSpan? timeout = null,
        CancellationToken ct = default, Stream? standardOutput = null, Stream? standardError = null)
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

        var stdoutTask = CaptureOutputAsync(proc.StandardOutput, standardOutput);
        var stderrTask = CaptureOutputAsync(proc.StandardError, standardError);
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

        async Task<string> CaptureOutputAsync(StreamReader reader, Stream? destination)
        {
            if (destination is null)
                return await reader.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            await reader.BaseStream.CopyToAsync(destination, linked.Token).ConfigureAwait(false);
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
