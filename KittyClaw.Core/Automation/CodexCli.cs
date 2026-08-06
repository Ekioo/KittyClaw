using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>Detection and model catalog for OpenAI's Codex CLI.</summary>
public static class CodexCli
{
    public const string ModelPrefix = "codex:";
    private static Lazy<string?> _binary = new(ResolveBinary);

    public static string? Binary => _binary.Value;
    public static bool IsInstalled => _binary.Value is not null;

    public static bool IsCodexModel(string? model) =>
        model?.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static string ToCliModel(string model) =>
        IsCodexModel(model) ? model[ModelPrefix.Length..] : model;

    /// <summary>
    /// Models offered in selectors when Codex is installed. Codex has no stable model-list
    /// command; keep this small and current. Users may still persist a custom model id.
    /// </summary>
    public static readonly IReadOnlyList<string> Models =
    [
        "codex:gpt-5.6-sol",
        "codex:gpt-5.6-terra",
        "codex:gpt-5.6-luna",
    ];

    public static Task<IReadOnlyList<string>> ListModelsAsync() =>
        Task.FromResult(IsInstalled ? Models : (IReadOnlyList<string>)Array.Empty<string>());

    private static string? ResolveBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KITTYCLAW_CODEX_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && IsUsableBinary(fromEnv))
            return fromEnv;

        var names = OperatingSystem.IsWindows()
            ? new[] { "codex.exe", "codex.cmd", "codex.bat", "codex" }
            : new[] { "codex" };
        var baseDir = AppContext.BaseDirectory;

        foreach (var name in names)
        {
            var sibling = Path.Combine(baseDir, name);
            if (IsUsableBinary(sibling)) return sibling;
            var tools = Path.Combine(baseDir, "tools", name);
            if (IsUsableBinary(tools)) return tools;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (OperatingSystem.IsWindows())
            {
                var npmNative = Path.Combine(dir, "node_modules", "@openai", "codex",
                    "node_modules", "@openai", "codex-win32-x64", "vendor",
                    "x86_64-pc-windows-msvc", "bin", "codex.exe");
                if (IsUsableBinary(npmNative)) return npmNative;
            }

            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (IsUsableBinary(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }
        }

        return null;
    }

    private static bool IsUsableBinary(string path) =>
        File.Exists(path) && ProcessRunner.ProbeSync(path, "--version", TimeSpan.FromSeconds(5));

    internal static void ResetForTests() =>
        _binary = new Lazy<string?>(ResolveBinary);
}
