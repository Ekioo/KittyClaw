using System.Diagnostics;
using System.Reflection;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Services;

public sealed class AgentsTemplateService
{
    private const string AgentsPrefix = "KittyClaw.Core.AgentsTemplate/";
    private const string RootPrefix = "KittyClaw.Core.AgentsTemplateRoot/";
    private readonly Assembly _assembly = typeof(AgentsTemplateService).Assembly;

    public IReadOnlyList<string> RelativePaths() => EnumerateWithPrefix(AgentsPrefix);
    public IReadOnlyList<string> RootRelativePaths() => EnumerateWithPrefix(RootPrefix);

    private IReadOnlyList<string> EnumerateWithPrefix(string prefix)
    {
        var names = _assembly.GetManifestResourceNames();
        var list = new List<string>();
        foreach (var n in names)
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal))
                list.Add(n.Substring(prefix.Length).Replace('\\', '/'));
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private byte[] ReadAsset(string prefix, string relativePath)
    {
        var allNames = _assembly.GetManifestResourceNames();
        var name = prefix + relativePath.Replace('/', '\\');
        if (!allNames.Contains(name))
            name = prefix + relativePath.Replace('\\', '/');
        using var s = _assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded asset not found: {name}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public IReadOnlyList<string> AgentSlugs()
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in RelativePaths())
        {
            var parts = rel.Split('/');
            if (parts.Length == 2 && parts[1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                slugs.Add(parts[0]);
        }
        return slugs.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public List<string> DetectConflicts(string workspacePath)
    {
        var conflicts = new List<string>();
        foreach (var rel in RelativePaths())
        {
            var dest = Path.Combine(workspacePath, ".agents", rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) conflicts.Add(".agents/" + rel);
        }
        foreach (var rel in RootRelativePaths())
        {
            var dest = Path.Combine(workspacePath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest)) conflicts.Add(rel);
        }
        return conflicts;
    }

    public async Task<InitializeResult> InitializeAsync(string workspacePath, bool overwriteConflicts)
    {
        var written = new List<string>();
        var skipped = new List<string>();

        Directory.CreateDirectory(workspacePath);

        foreach (var rel in RelativePaths())
        {
            var dest = Path.Combine(workspacePath, ".agents", rel.Replace('/', Path.DirectorySeparatorChar));
            var exists = File.Exists(dest);
            if (exists && !overwriteConflicts)
            {
                skipped.Add(".agents/" + rel);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var bytes = ReadAsset(AgentsPrefix, rel);
            await File.WriteAllBytesAsync(dest, bytes);
            written.Add(".agents/" + rel);
        }

        foreach (var rel in RootRelativePaths())
        {
            var dest = Path.Combine(workspacePath, rel.Replace('/', Path.DirectorySeparatorChar));
            var exists = File.Exists(dest);
            if (exists && !overwriteConflicts)
            {
                skipped.Add(rel);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var bytes = ReadAsset(RootPrefix, rel);
            await File.WriteAllBytesAsync(dest, bytes);
            written.Add(rel);
        }

        var gitInitResult = GitInitResult.NotAttempted;
        var gitDir = Path.Combine(workspacePath, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
        {
            if (!IsGitAvailable())
            {
                gitInitResult = GitInitResult.GitMissing;
            }
            else
            {
                var (ok, _) = RunProcess("git", "init", workspacePath);
                gitInitResult = ok ? GitInitResult.Created : GitInitResult.Failed;
            }
        }
        else
        {
            gitInitResult = GitInitResult.AlreadyExists;
        }

        return new InitializeResult(written, skipped, gitInitResult);
    }

    public bool IsGitAvailable()
    {
        try
        {
            var (ok, _) = RunProcess("git", "--version", workingDirectory: null);
            return ok;
        }
        catch { return false; }
    }

    public bool IsClaudeAvailable()
    {
        try
        {
            var (ok, _) = RunProcess("claude", "--version", workingDirectory: null);
            return ok;
        }
        catch { return false; }
    }

    public bool IsCodexAvailable() => IsCommandAvailable(CodexCli.Binary ?? "codex", "--version");

    public bool IsGrokAvailable() => IsCommandAvailable(GrokCli.Binary ?? "grok", "--version");

    public bool IsOllamaAvailable() => IsCommandAvailable("ollama", "--version");

    private static bool IsCommandAvailable(string command, string arguments)
    {
        try
        {
            var (ok, _) = RunProcess(command, arguments, workingDirectory: null);
            return ok;
        }
        catch { return false; }
    }

    private static (bool Ok, string Output) RunProcess(string file, string args, string? workingDirectory)
    {
        try
        {
            var res = ProcessRunner.RunAsync(file, args, workingDirectory, TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            return (res.Success, res.Stdout + res.Stderr);
        }
        catch { return (false, ""); }
    }

    public enum GitInitResult { NotAttempted, AlreadyExists, Created, GitMissing, Failed }

    public sealed record InitializeResult(List<string> Written, List<string> Skipped, GitInitResult GitInit);
}
