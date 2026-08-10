using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Suspends the provider process itself while a runtime approval is pending. Stream gating alone
/// is insufficient because a child process can keep executing after KittyClaw stops reading its
/// output. The logical gate on <see cref="AgentRun"/> remains responsible for correlation and
/// completion ordering; this class enforces the execution boundary.
/// </summary>
internal sealed class ProcessApprovalGate : IDisposable
{
    private readonly Process _process;
    private readonly AgentRun _run;
    private readonly object _lock = new();
    private bool _suspended;
    private bool _disposed;

    public ProcessApprovalGate(Process process, AgentRun run)
    {
        _process = process;
        _run = run;
        _run.ApprovalWaitChanged += OnApprovalWaitChanged;
        if (_run.AwaitingApprovalRequestId is not null)
            SetSuspended(true);
    }

    private void OnApprovalWaitChanged(bool waiting) => SetSuspended(waiting);

    private void SetSuspended(bool suspend)
    {
        lock (_lock)
        {
            if (_disposed || _suspended == suspend || HasExited()) return;
            if (!TrySetProcessSuspended(_process, suspend))
            {
                _run.Push(new StreamEvent(DateTime.UtcNow, "error",
                    suspend
                        ? "Unable to suspend the provider process at the approval boundary"
                        : "Unable to resume the provider process after approval"));
                if (suspend) _run.Cancellation.Cancel();
                return;
            }
            _suspended = suspend;
        }
    }

    private bool HasExited()
    {
        try { return _process.HasExited; }
        catch { return true; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _run.ApprovalWaitChanged -= OnApprovalWaitChanged;
            if (_suspended && !HasExited())
                TrySetProcessSuspended(_process, suspend: false);
            _suspended = false;
            _disposed = true;
        }
    }

    private static bool TrySetProcessSuspended(Process process, bool suspend)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = suspend ? NtSuspendProcess(process.Handle) : NtResumeProcess(process.Handle);
                return status >= 0;
            }
            catch { return false; }
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var signal = OperatingSystem.IsMacOS()
                ? (suspend ? MacOsSigStop : MacOsSigCont)
                : (suspend ? LinuxSigStop : LinuxSigCont);
            try { return kill(process.Id, signal) == 0; }
            catch { return false; }
        }

        return false;
    }

    private const int LinuxSigStop = 19;
    private const int LinuxSigCont = 18;
    private const int MacOsSigStop = 17;
    private const int MacOsSigCont = 19;

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
