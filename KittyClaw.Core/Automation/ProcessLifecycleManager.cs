using System.Diagnostics;
using System.Text;

namespace KittyClaw.Core.Automation;

/// <summary>Resolves the claude binary and constructs ProcessStartInfo for a claude subprocess.</summary>
internal static class ProcessLifecycleManager
{
    // Resolved once per process. Search order:
    //   0. KITTYCLAW_CLAUDE_BIN env var (escape hatch / QaRunner injection)
    //   1. Sibling of host exe: <baseDir>/claude(.exe)
    //   2. <baseDir>/tools/claude(.exe)
    //   3. "claude" — resolved via PATH (production default)
    private static readonly Lazy<string> _claudeBinary = new(ResolveClaudeBinary);

    internal static string ClaudeBinary => _claudeBinary.Value;

    internal static string ResolveApiUrl()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls))
        {
            var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(first))
                return first.TrimEnd('/');
        }
        return "http://localhost:5230";
    }

    // Locator for the orchestrator's own Web exe. Works in both publish layout (exe sibling of the
    // running assembly) and `dotnet watch` dev (bin dir contains the exe even though the loader is
    // dotnet.exe). Returns a forward-slashed path so bash conditionals in skills work cleanly on Windows.
    internal static string? ResolveSelfWebExe()
    {
        var name = OperatingSystem.IsWindows() ? "KittyClaw.Web.exe" : "KittyClaw.Web";
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(sibling)) return sibling.Replace('\\', '/');
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            processPath.EndsWith(name, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
            return processPath.Replace('\\', '/');
        return null;
    }

    // Returns the QaRunner exe only when it sits next to the Web exe (publish layout). In dev each
    // project has its own bin/, so this returns null and the qa-tester skill falls back to dotnet run.
    internal static string? ResolveSiblingQaRunner()
    {
        var name = OperatingSystem.IsWindows() ? "KittyClaw.QaRunner.exe" : "KittyClaw.QaRunner";
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(sibling) ? sibling.Replace('\\', '/') : null;
    }

    private static string ResolveClaudeBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var baseDir = AppContext.BaseDirectory;

        var sibling = Path.Combine(baseDir, exe);
        if (File.Exists(sibling)) return sibling;

        var tools = Path.Combine(baseDir, "tools", exe);
        if (File.Exists(tools)) return tools;

        return "claude";
    }

    internal static ProcessStartInfo BuildProcessStartInfo(ClaudeRunContext ctx, IList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _claudeBinary.Value,
            WorkingDirectory = ctx.WorkspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Force UTF-8 on all three streams. .NET's default on Windows is the OEM code
            // page (CP850/CP1252), which mangles every accented character in chat prompts
            // sent to claude and every accented character in claude's reply going back.
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["CLAUDE_AGENT"] = ctx.AgentName;
        // Tell skills which API URL to talk to. Skills resolve `${KITTYCLAW_API_URL:-http://localhost:5230}`
        // so they hit the *current* host instance even when running on a non-default port (e.g. an
        // isolated test instance spawned by KittyClaw.QaRunner).
        psi.Environment["KITTYCLAW_API_URL"] = ResolveApiUrl();
        // Locate the orchestrator's own Web exe (and sibling QaRunner exe in the publish layout) so
        // skills test the same build that's orchestrating them — not whatever happens to be on disk.
        var selfWebExe = ResolveSelfWebExe();
        if (selfWebExe is not null) psi.Environment["KITTYCLAW_WEB_EXE"] = selfWebExe;
        var siblingQaRunner = ResolveSiblingQaRunner();
        if (siblingQaRunner is not null) psi.Environment["KITTYCLAW_QARUNNER_EXE"] = siblingQaRunner;
        foreach (var kv in ctx.Env) psi.Environment[kv.Key] = kv.Value;

        return psi;
    }
}
