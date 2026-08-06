using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>Detection and model catalog for Mistral's Vibe CLI.</summary>
public static class MistralCli
{
    public const string ModelPrefix = "mistral:";
    private static Lazy<string?> _binary = new(ResolveBinary);

    public static string? Binary => _binary.Value;
    public static bool IsInstalled => _binary.Value is not null;

    public static bool IsMistralModel(string? model) =>
        model?.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static string ToCliModel(string model) =>
        IsMistralModel(model) ? model[ModelPrefix.Length..] : model;

    /// <summary>Stable aliases shipped by Vibe. The CLI resolves them to its current hosted models.</summary>
    public static readonly IReadOnlyList<string> Models =
    [
        "mistral:mistral-medium-3.5",
        "mistral:devstral-small",
    ];

    public static Task<IReadOnlyList<string>> ListModelsAsync() =>
        Task.FromResult(IsInstalled ? Models : (IReadOnlyList<string>)Array.Empty<string>());

    private static string? ResolveBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KITTYCLAW_MISTRAL_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && IsUsableBinary(fromEnv)) return fromEnv;

        var names = OperatingSystem.IsWindows()
            ? new[] { "vibe.exe", "vibe.cmd", "vibe.bat", "vibe" }
            : new[] { "vibe" };
        var baseDir = AppContext.BaseDirectory;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var name in names)
        {
            var sibling = Path.Combine(baseDir, name);
            if (IsUsableBinary(sibling)) return sibling;
            var tools = Path.Combine(baseDir, "tools", name);
            if (IsUsableBinary(tools)) return tools;
            if (!string.IsNullOrWhiteSpace(home))
            {
                var uv = Path.Combine(home, ".local", "bin", name);
                if (IsUsableBinary(uv)) return uv;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var name in names)
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (IsUsableBinary(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
        return null;
    }

    private static bool IsUsableBinary(string path) =>
        File.Exists(path) && ProcessRunner.ProbeSync(path, "--version", TimeSpan.FromSeconds(5));

    internal static void ResetForTests() => _binary = new Lazy<string?>(ResolveBinary);
}
