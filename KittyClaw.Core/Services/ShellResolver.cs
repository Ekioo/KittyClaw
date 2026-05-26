using System.Runtime.InteropServices;

namespace KittyClaw.Core.Services;

internal static class ShellResolver
{
    internal static string ResolvePowerShell()
    {
        if (TryFindOnPath("pwsh")) return "pwsh";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TryFindOnPath("powershell"))
            return "powershell";
        return "pwsh";
    }

    internal static bool TryFindOnPath(string exe)
    {
        var pathExt = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { ".exe", ".cmd", "" }
            : new[] { "" };
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in paths)
            foreach (var ext in pathExt)
                if (File.Exists(Path.Combine(dir, exe + ext))) return true;
        return false;
    }
}
