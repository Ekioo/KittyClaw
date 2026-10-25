using System.Diagnostics;
using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Services;

/// <summary>Opens the recorded artifact directory on the KittyClaw host.</summary>
public sealed class RunWorkingFilesService(AgentRunRegistry runs)
{
    internal static ProcessStartInfo BuildOpenStartInfo(AgentRun run)
    {
        var path = run.WorkingFilesPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidOperationException("The working files folder is unavailable or has been removed.");
        var executable = OperatingSystem.IsWindows() ? "explorer.exe"
            : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(path);
        return info;
    }

    public void Open(string projectSlug, string runId)
    {
        var run = runs.Get(runId);
        if (run is null || run.ProjectSlug != projectSlug)
            throw new InvalidOperationException("The execution is unavailable.");
        using var process = Process.Start(BuildOpenStartInfo(run));
    }
}
