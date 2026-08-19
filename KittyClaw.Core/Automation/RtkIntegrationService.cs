using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public sealed record RtkIntegrationStatus(
    bool Enabled,
    bool Available,
    string Mode,
    string? Version = null,
    string? Reason = null);

/// <summary>
/// Resolves the optional RTK output optimizer without installing it, changing its
/// configuration, or wrapping the provider process and its structured output stream.
/// </summary>
public sealed partial class RtkIntegrationService
{
    private static readonly TimeSpan ProbeCacheDuration = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, CachedProbe> ProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ProjectService _projects;
    private readonly Func<CancellationToken, Task<RtkProbeResult>>? _probeOverride;

    internal sealed record RtkProbeResult(bool Available, string? Version, string? Reason);
    private sealed record CachedProbe(RtkProbeResult Result, DateTime ExpiresAt);

    public RtkIntegrationService(ProjectService projects) : this(projects, null) { }

    internal RtkIntegrationService(
        ProjectService projects,
        Func<CancellationToken, Task<RtkProbeResult>>? probeOverride)
    {
        _projects = projects;
        _probeOverride = probeOverride;
    }

    public async Task<RtkIntegrationStatus?> GetStatusAsync(string projectSlug, CancellationToken ct = default)
    {
        var project = await _projects.GetProjectAsync(projectSlug);
        if (project is null) return null;
        if (!project.RtkEnabled)
            return new RtkIntegrationStatus(false, false, "disabled", Reason: "Project opt-in is disabled.");

        var probe = _probeOverride is null ? await ProbeAsync(ct) : await _probeOverride(ct);
        return probe.Available
            ? new RtkIntegrationStatus(true, true, "instruction", probe.Version)
            : new RtkIntegrationStatus(true, false, "unavailable", Reason: probe.Reason);
    }

    internal static string AppendInstructions(string prompt, RtkIntegrationStatus? status)
    {
        if (status is not { Enabled: true, Available: true }) return prompt;
        return $$"""
            {{prompt}}

            <kittyclaw_rtk>
            RTK {{status.Version ?? "(version unknown)"}} is available for this project. For shell commands with potentially large output, prefer an RTK-supported equivalent such as `rtk git status`, `rtk git diff`, `rtk grep`, `rtk read`, or `rtk test`. Do not use RTK when exact or byte-for-byte output is required. If RTK fails or omits needed detail, rerun the original command unchanged. Never install RTK, run `rtk init`, enable telemetry, or modify RTK/user/repository configuration.
            </kittyclaw_rtk>
            """;
    }

    internal static void ApplyEnvironment(ProcessStartInfo startInfo, RtkIntegrationStatus? status)
    {
        if (status is { Enabled: true })
            startInfo.Environment["RTK_TELEMETRY_DISABLED"] = "1";
    }

    private static async Task<RtkProbeResult> ProbeAsync(CancellationToken ct)
    {
        var binary = Environment.GetEnvironmentVariable("KITTYCLAW_RTK_BIN");
        if (string.IsNullOrWhiteSpace(binary)) binary = OperatingSystem.IsWindows() ? "rtk.exe" : "rtk";

        var now = DateTime.UtcNow;
        if (ProbeCache.TryGetValue(binary, out var cached) && cached.ExpiresAt > now)
            return cached.Result;

        var result = await ProbeBinaryAsync(binary, ct);
        ProbeCache[binary] = new CachedProbe(result, now + ProbeCacheDuration);
        return result;
    }

    private static async Task<RtkProbeResult> ProbeBinaryAsync(string binary, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo(binary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null) return new RtkProbeResult(false, null, "RTK could not be started.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new RtkProbeResult(false, null, "RTK version probe timed out.");
            }

            var output = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
            var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
            if (process.ExitCode != 0)
                return new RtkProbeResult(false, null, string.IsNullOrWhiteSpace(error)
                    ? $"RTK version probe exited with code {process.ExitCode}."
                    : "RTK version probe failed.");

            var match = VersionRegex().Match(output);
            return match.Success
                ? new RtkProbeResult(true, match.Groups[1].Value, null)
                : new RtkProbeResult(false, null, "The detected rtk executable returned an unrecognized version.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new RtkProbeResult(false, null, "RTK is not installed or is not available on PATH.");
        }
    }

    [GeneratedRegex(@"\brtk\s+v?([0-9]+(?:\.[0-9]+){1,3}(?:[-+][^\s]+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}
