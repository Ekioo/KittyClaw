using System.Text.RegularExpressions;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public sealed record CliVersionMetadata(
    string Provider,
    string Binary,
    string? Version,
    string? ExpectedVersion,
    string Status,
    string? Failure,
    bool Mismatch);

internal static partial class CliVersionProbe
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    internal static string BinaryFor(CliProvider provider) => provider switch
    {
        CliProvider.Codex => CodexCli.Binary ?? "codex",
        CliProvider.Grok => GrokCli.Binary ?? "grok",
        _ => ProcessLifecycleManager.ClaudeBinary,
    };

    internal static string? ExpectedVersionFor(CliProvider provider) =>
        Environment.GetEnvironmentVariable(provider switch
        {
            CliProvider.Codex => "KITTYCLAW_CODEX_EXPECTED_VERSION",
            CliProvider.Grok => "KITTYCLAW_GROK_EXPECTED_VERSION",
            _ => "KITTYCLAW_CLAUDE_EXPECTED_VERSION",
        });

    internal static async Task<CliVersionMetadata> ProbeAsync(
        CliProvider provider,
        string binary,
        string? expectedVersion,
        Func<string, string, TimeSpan, Task<ProcessRunner.ProcessResult>>? execute = null)
    {
        var providerName = provider.ToString().ToLowerInvariant();
        var safeBinary = SafeBinaryIdentity(binary);
        try
        {
            var result = execute is null
                ? await ProcessRunner.RunAsync(binary, "--version", timeout: Timeout)
                : await execute(binary, "--version", Timeout);
            if (result.TimedOut)
                return Failed("timeout");
            if (result.ExitCode != 0)
                return Failed($"version command exited with code {result.ExitCode}");

            var output = string.Join('\n', result.Stdout, result.Stderr).Trim();
            var match = VersionPattern().Match(output);
            if (!match.Success)
                return Failed("unsupported version output");

            var version = match.Value;
            var mismatch = !string.IsNullOrWhiteSpace(expectedVersion)
                && !string.Equals(version, expectedVersion.Trim(), StringComparison.OrdinalIgnoreCase);
            return new CliVersionMetadata(providerName, safeBinary, version, expectedVersion,
                mismatch ? "mismatch" : "detected", null, mismatch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"probe failed: {ex.Message}");
        }

        CliVersionMetadata Failed(string failure) =>
            new(providerName, safeBinary, null, expectedVersion, "failed", failure, false);
    }

    private static string SafeBinaryIdentity(string binary)
    {
        if (!Path.IsPathRooted(binary)) return binary;
        try { return Path.GetFullPath(binary); }
        catch { return Path.GetFileName(binary); }
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])v?\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
