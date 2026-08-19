using System.Diagnostics;
using System.Text.Json;

namespace KittyClaw.ClaudeMock;

internal sealed record HookSettings(string? PreToolUseCommand, string? PostToolUseCommand)
{
    /// <summary>Parses the `hooks` section of a `--settings` file the way KittyClaw writes it
    /// (one command per PreToolUse/PostToolUse matcher group).</summary>
    public static HookSettings Load(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
            return new(null, null);
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return new(FirstCommand(doc.RootElement, "PreToolUse"), FirstCommand(doc.RootElement, "PostToolUse"));
    }

    private static string? FirstCommand(JsonElement root, string eventName)
    {
        if (!root.TryGetProperty("hooks", out var hooks) || !hooks.TryGetProperty(eventName, out var groups)
            || groups.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("hooks", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in entries.EnumerateArray())
                if (entry.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
                    return command.GetString();
        }
        return null;
    }
}

internal static class HookRunner
{
    /// <summary>Runs a hook command like real claude does (through the platform shell), feeding the
    /// payload on stdin. Returns true only for an explicit allow verdict from a clean exit — the
    /// mock is fail-closed on every other outcome so tests can prove no effect leaks through.</summary>
    public static async Task<bool> RunPreToolUseAsync(string command, string payload)
    {
        var (exitCode, stdout) = await RunAsync(command, payload);
        if (exitCode != 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            return doc.RootElement.TryGetProperty("hookSpecificOutput", out var output)
                && output.TryGetProperty("permissionDecision", out var decision)
                && string.Equals(decision.GetString(), "allow", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task RunPostToolUseAsync(string command, string payload)
    {
        try { await RunAsync(command, payload); }
        catch { /* Post hooks never influence the mock's stream. */ }
    }

    private static async Task<(int ExitCode, string Stdout)> RunAsync(string command, string payload)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // cmd.exe needs the raw command line (ArgumentList quoting would mangle embedded quotes).
        if (OperatingSystem.IsWindows()) psi.Arguments = $"/s /c \"{command}\"";
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }

        using var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteAsync(payload);
        proc.StandardInput.Close();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await proc.WaitForExitAsync(timeout.Token);
        return (proc.ExitCode, await stdoutTask + await Discard(stderrTask));
    }

    private static async Task<string> Discard(Task<string> stderr)
    {
        _ = await stderr;
        return "";
    }
}
