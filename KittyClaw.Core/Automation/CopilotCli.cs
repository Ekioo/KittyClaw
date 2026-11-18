using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>Detection and model catalog for GitHub Copilot CLI.</summary>
public static class CopilotCli
{
    public const string ModelPrefix = "copilot:";
    private static Lazy<string?> _binary = new(ResolveBinary);

    public static string? Binary => _binary.Value;
    public static bool IsInstalled => _binary.Value is not null;

    public static bool IsCopilotModel(string? model) =>
        model?.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static string? ToCliModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!IsCopilotModel(model)) return model;

        var cliModel = model[ModelPrefix.Length..];
        return cliModel.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : cliModel;
    }

    /// <summary>
    /// GitHub's published Copilot model identifiers. Account and organization policies may
    /// expose a smaller set; "default" delegates selection to the installed Copilot CLI.
    /// </summary>
    public static readonly IReadOnlyList<string> Models =
    [
        "copilot:default",
        "copilot:gpt-6.2-astra",
        "copilot:gpt-6.2-sol",
        "copilot:gpt-6.1-sol",
        "copilot:gpt-6.1-terra",
        "copilot:gpt-6-astra",
        "copilot:gpt-6-luna",
        "copilot:gpt-6-sol",
        "copilot:gpt-5.6-luna",
        "copilot:gpt-5.6-sol",
        "copilot:gpt-5.4-nano",
        "copilot:gpt-5.3-codex",
        "copilot:claude-opus-5.5",
        "copilot:claude-opus-4.8",
        "copilot:claude-sonnet-5.5",
        "copilot:claude-sonnet-5",
        "copilot:claude-sonnet-4.6",
        "copilot:claude-haiku-5.5",
        "copilot:claude-haiku-4.5",
        "copilot:gemini-4-argon",
        "copilot:gemini-4-carbon",
        "copilot:gemini-4-hydrogen",
        "copilot:gemini-3.8-flash",
        "copilot:mai-code-2",
        "copilot:mai-code-2-flash",
        "copilot:mai-code-1.1-flash",
        "copilot:kimi-k3",
        "copilot:grok-4.7",
        "copilot:grok-4.6",
        "copilot:grok-4.5",
    ];

    public static Task<IReadOnlyList<string>> ListModelsAsync() =>
        Task.FromResult(IsInstalled ? Models : (IReadOnlyList<string>)Array.Empty<string>());

    private static string? ResolveBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KITTYCLAW_COPILOT_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && IsUsableBinary(fromEnv))
            return fromEnv;

        var names = OperatingSystem.IsWindows()
            ? new[] { "copilot.exe", "copilot.cmd", "copilot.bat", "copilot" }
            : new[] { "copilot" };
        var baseDir = AppContext.BaseDirectory;

        foreach (var name in names)
        {
            var sibling = Path.Combine(baseDir, name);
            if (IsUsableBinary(sibling)) return sibling;
            var tools = Path.Combine(baseDir, "tools", name);
            if (IsUsableBinary(tools)) return tools;
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
