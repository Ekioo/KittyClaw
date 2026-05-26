using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Confines a process and every process it spawns to a Windows Job Object configured to kill all
/// members when the job handle closes (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>).
///
/// Why this exists: KittyClaw runs claude with redirected stdout/stderr. If the agent backgrounds
/// a process (e.g. a dev server), that child inherits the pipe's write handle and can outlive
/// claude — so the pipe never reaches EOF, the stdout/stderr pump tasks never finish, and the run
/// stays stuck Running forever. Closing the job terminates every descendant, releasing the pipe so
/// EOF arrives deterministically. It also prevents leaked orphan processes.
///
/// No-op on non-Windows platforms (returns null from <see cref="TryCreateAndAssign"/>).
/// </summary>
internal sealed class ProcessJobObject : IDisposable
{
    private IntPtr _handle = IntPtr.Zero;

    /// <summary>Creates a kill-on-close job and assigns <paramref name="proc"/> to it.
    /// Returns null on non-Windows platforms or if the OS rejects the assignment, in which case
    /// callers fall back to <see cref="Process.Kill(bool)"/> and a bounded pump drain.</summary>
    public static ProcessJobObject? TryCreateAndAssign(Process proc)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = new ProcessJobObject();
        try
        {
            if (job.Initialize() && job.Assign(proc)) return job;
        }
        catch { /* fall through to dispose + null */ }
        job.Dispose();
        return null;
    }

    [SupportedOSPlatform("windows")]
    private bool Initialize()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) return false;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            return SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)length);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [SupportedOSPlatform("windows")]
    private bool Assign(Process proc) => AssignProcessToJobObject(_handle, proc.Handle);

    /// <summary>Closes the job handle. With KILL_ON_JOB_CLOSE this terminates every process still
    /// in the job. Idempotent.</summary>
    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
