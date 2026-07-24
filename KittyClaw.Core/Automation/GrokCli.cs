using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Detection and model discovery for xAI's Grok Build CLI (the `grok` binary).
/// When the CLI is installed on the host, members can use Grok models: dispatch
/// runs the `grok` subprocess instead of `claude` (see <see cref="ModelRouting"/>).
/// </summary>
public static class GrokCli
{
    // Resolved once per process, mirroring ProcessLifecycleManager.ResolveClaudeBinary. Search order:
    //   0. KITTYCLAW_GROK_BIN env var (escape hatch / test injection)
    //   1. Sibling of host exe: <baseDir>/grok(.exe)
    //   2. <baseDir>/tools/grok(.exe)
    //   3. PATH probe — unlike claude we probe explicitly (checking .exe/.cmd/.bat on Windows)
    //      because "installed" drives UI visibility, not just spawn-time resolution.
    private static Lazy<string?> _binary = new(ResolveBinary);

    /// <summary>Full path to the grok binary, or null when not installed.</summary>
    public static string? Binary => _binary.Value;

    public static bool IsInstalled => _binary.Value is not null;

    /// <summary>True when the model name targets the Grok CLI (xAI ids all start with "grok-").</summary>
    public static bool IsGrokModel(string? model) =>
        model is not null && model.StartsWith("grok-", StringComparison.OrdinalIgnoreCase);

    // Shown when `grok models` cannot be run or its output yields nothing parseable.
    // Per https://docs.x.ai/developers/models: grok-build-0.1 is the CLI's own coding model,
    // grok-4.5 the recommended code/chat model, grok-4.3 the stable general-purpose one.
    internal static readonly IReadOnlyList<string> FallbackModels = new[] { "grok-build-0.1", "grok-4.5", "grok-4.3" };

    private static Task<IReadOnlyList<string>>? _modelsTask;

    /// <summary>
    /// Lists the model ids the installed grok CLI offers, by parsing `grok models` output.
    /// Cached for the process lifetime. Returns an empty list when the CLI is not installed.
    /// </summary>
    public static Task<IReadOnlyList<string>> ListModelsAsync()
    {
        if (!IsInstalled) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        return _modelsTask ??= QueryModelsAsync();
    }

    private static async Task<IReadOnlyList<string>> QueryModelsAsync()
    {
        try
        {
            var res = await ProcessRunner.RunAsync(
                _binary.Value!, "models", Path.GetTempPath(), TimeSpan.FromSeconds(15));
            if (res.Success)
            {
                var models = ParseModelList(res.Stdout);
                if (models.Count > 0) return models;
            }
        }
        catch { /* discovery is best-effort — fall back to the static list */ }
        return FallbackModels;
    }

    // `grok models` prints a human-readable table; model ids are the tokens that look like
    // "grok-…". Grab the first such token per line, dedupe, keep output order.
    internal static IReadOnlyList<string> ParseModelList(string stdout)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in stdout.Split('\n'))
        {
            foreach (var token in rawLine.Split(' ', '\t', '|', ',').Select(t => t.Trim().TrimEnd('*')))
            {
                if (token.Length <= 5 || !token.StartsWith("grok-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or ':')) continue;
                if (seen.Add(token)) result.Add(token);
                break;
            }
        }
        return result;
    }

    private static string? ResolveBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KITTYCLAW_GROK_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var names = OperatingSystem.IsWindows()
            ? new[] { "grok.exe", "grok.cmd", "grok.bat" }
            : new[] { "grok" };
        var baseDir = AppContext.BaseDirectory;

        foreach (var name in names)
        {
            var sibling = Path.Combine(baseDir, name);
            if (File.Exists(sibling)) return sibling;
            var tools = Path.Combine(baseDir, "tools", name);
            if (File.Exists(tools)) return tools;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    /// <summary>Re-resolves the binary and clears the model cache. Test hook — resolution is otherwise cached per process.</summary>
    internal static void ResetForTests()
    {
        _binary = new Lazy<string?>(ResolveBinary);
        _modelsTask = null;
    }
}
