using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Executes optional tile scripts (.ps1, .sh, .js, .py) whose stdout is written to the tile's
/// result file. Runs with full user rights; working directory is the project workspace root.
/// </summary>
public sealed class DashboardScriptRunner
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ps1", ".sh", ".js", ".py" };

    private readonly ILogger<DashboardScriptRunner> _logger;

    public DashboardScriptRunner(ILogger<DashboardScriptRunner> logger)
    {
        _logger = logger;
    }

    public static bool IsSupported(string scriptFileName)
    {
        var ext = Path.GetExtension(scriptFileName);
        return SupportedExtensions.Contains(ext);
    }

    /// <summary>
    /// Runs the given script file and returns its stdout, or null on failure.
    /// <paramref name="scriptPath"/> is the absolute path to the script.
    /// <paramref name="workspace"/> is used as the working directory.
    /// </summary>
    public async Task<ScriptResult> RunAsync(string scriptPath, string workspace, CancellationToken ct)
    {
        var ext = Path.GetExtension(scriptPath);

        string interpreter;
        string args;

        switch (ext.ToLowerInvariant())
        {
            case ".ps1":
                interpreter = ResolvePowerShell();
                args = $"-NonInteractive -File \"{scriptPath}\"";
                break;
            case ".sh":
                interpreter = ResolveBash();
                args = $"\"{scriptPath}\"";
                break;
            case ".js":
                interpreter = "node";
                args = $"\"{scriptPath}\"";
                break;
            case ".py":
                interpreter = "python";
                args = $"\"{scriptPath}\"";
                break;
            default:
                return ScriptResult.FromConfigError($"Unsupported script extension: {ext}. Supported: .ps1, .sh, .js, .py");
        }

        _logger.LogInformation("Running dashboard script {Script} with {Interpreter}", scriptPath, interpreter);

        var psi = new ProcessStartInfo
        {
            FileName = interpreter,
            Arguments = args,
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {interpreter}");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            _logger.LogInformation("Script {Script} exited with code {Code}", scriptPath, process.ExitCode);

            if (process.ExitCode != 0)
                return ScriptResult.Failure(process.ExitCode, stderr);

            return ScriptResult.Success(stdout, stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = $"Failed to start interpreter '{interpreter}': {ex.Message}";
            _logger.LogWarning(ex, "Dashboard script {Script} could not be started", scriptPath);
            return ScriptResult.FromConfigError(msg);
        }
    }

    private static string ResolvePowerShell()
    {
        // Prefer pwsh (PowerShell 7+); fall back to powershell on Windows.
        if (TryFindOnPath("pwsh")) return "pwsh";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TryFindOnPath("powershell"))
            return "powershell";
        return "pwsh";
    }

    private static string ResolveBash()
    {
        if (TryFindOnPath("bash")) return "bash";
        // Git Bash on Windows common location.
        var gitBash = @"C:\Program Files\Git\bin\bash.exe";
        if (File.Exists(gitBash)) return gitBash;
        return "bash";
    }

    private static bool TryFindOnPath(string exe)
    {
        var pathExt = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { ".exe", ".cmd", "" }
            : new[] { "" };
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in paths)
        {
            foreach (var ext in pathExt)
            {
                if (File.Exists(Path.Combine(dir, exe + ext))) return true;
            }
        }
        return false;
    }
}

public sealed record ScriptResult(bool IsSuccess, string Stdout, string Stderr, int ExitCode, string? ConfigError)
{
    public static ScriptResult Success(string stdout, string stderr) =>
        new(true, stdout, stderr, 0, null);

    public static ScriptResult Failure(int exitCode, string stderr) =>
        new(false, "", stderr, exitCode, null);

    public static ScriptResult FromConfigError(string message) =>
        new(false, "", message, -1, message);
}
