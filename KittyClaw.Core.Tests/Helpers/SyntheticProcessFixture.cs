using System.Diagnostics;
using System.Text;

namespace KittyClaw.Core.Tests.Helpers;

/// <summary>Owns only synthetic Windows processes, identified by PID and start time.</summary>
internal sealed class SyntheticProcessFixture : IDisposable
{
    private readonly TempDir _temp = new();
    private string IdentityFile => Path.Combine(_temp.Path, "process.identity");

    public bool WasStarted
    {
        get
        {
            var parts = File.ReadAllText(IdentityFile).Split(':');
            return parts.Length == 2 && int.TryParse(parts[0], out _) && long.TryParse(parts[1], out _);
        }
    }

    public string RecordSelf => $"$self = [System.Diagnostics.Process]::GetCurrentProcess(); {Record("$self")}; ";

    public string SpawnInheritedChild
    {
        get
        {
            var command = Encode("Start-Sleep -Seconds 30");
            return "$info = New-Object System.Diagnostics.ProcessStartInfo; " +
                "$info.FileName = 'powershell.exe'; $info.UseShellExecute = $false; $info.CreateNoWindow = $true; " +
                $"$info.Arguments = '-NoProfile -NonInteractive -EncodedCommand {command}'; " +
                $"$child = [System.Diagnostics.Process]::Start($info); {Record("$child")}; ";
        }
    }

    private string Record(string process) =>
        $"[System.IO.File]::WriteAllText('{IdentityFile.Replace("'", "''")}', [string]{process}.Id + ':' + [string]{process}.StartTime.ToUniversalTime().Ticks)";

    public async Task<Process> WaitForProcessAsync()
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            var process = ReadProcess();
            if (process is not null) return process;
            await Task.Delay(20);
        }
        throw new TimeoutException("The synthetic process did not publish its identity.");
    }

    public static ProcessStartInfo Command(string windows, string unix)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in OperatingSystem.IsWindows()
                     ? new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(windows) }
                     : new[] { "-c", unix })
            info.ArgumentList.Add(arg);
        return info;
    }

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public void Stop()
    {
        using var process = ReadProcess();
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private Process? ReadProcess()
    {
        try
        {
            var parts = File.ReadAllText(IdentityFile).Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var ticks))
                return null;
            var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks == ticks) return process;
            process.Dispose();
        }
        catch (IOException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    public void Dispose()
    {
        Stop();
        _temp.Dispose();
    }
}
