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

    public async Task<InitializeResult> InitializeAsync(
        string workspacePath,
        bool overwriteConflicts,
        bool initializeGit = true)
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
        if (initializeGit)
        {
            var gitStatus = await InspectGitWorkspaceAsync(workspacePath);
            if (gitStatus.Kind == GitWorkspaceKind.Eligible)
            {
                var (ok, _) = await RunProcessAsync("git", "init", workspacePath);
                gitInitResult = ok ? GitInitResult.Created : GitInitResult.Failed;
            }
            else if (gitStatus.Kind == GitWorkspaceKind.GitUnavailable)
            {
                gitInitResult = GitInitResult.GitMissing;
            }
            else if (gitStatus.Kind is GitWorkspaceKind.Repository or GitWorkspaceKind.MetadataPresent)
            {
                gitInitResult = GitInitResult.AlreadyExists;
            }
        }

        return new InitializeResult(written, skipped, gitInitResult);
    }

    public Task<bool> IsGitAvailableAsync() => IsCommandAvailableAsync("git", "--version");

    public static async Task<GitWorkspaceInspection> InspectGitWorkspaceAsync(string workspacePath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!Directory.Exists(fullPath))
            return new(GitWorkspaceKind.DirectoryMissing, fullPath, null);

        var metadata = Path.Combine(fullPath, ".git");
        var hasMetadata = Directory.Exists(metadata) || File.Exists(metadata);
        if (!await IsCommandAvailableAsync("git", "--version"))
            return new(hasMetadata ? GitWorkspaceKind.MetadataPresent : GitWorkspaceKind.GitUnavailable, fullPath, null);

        var (insideRepository, output) = await RunProcessAsync("git", "rev-parse --show-toplevel", fullPath);
        if (insideRepository && !string.IsNullOrWhiteSpace(output))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output.Trim()));
            return new(GitWorkspaceKind.Repository, fullPath, root);
        }

        return new(hasMetadata ? GitWorkspaceKind.MetadataPresent : GitWorkspaceKind.Eligible, fullPath, null);
    }

    public Task<bool> IsClaudeAvailableAsync() => IsCommandAvailableAsync("claude", "--version");

    public Task<bool> IsCodexAvailableAsync() =>
        IsCommandAvailableAsync(CodexCli.Binary ?? "codex", "--version");

    public Task<bool> IsGrokAvailableAsync() =>
        IsCommandAvailableAsync(GrokCli.Binary ?? "grok", "--version");

    public Task<bool> IsMistralAvailableAsync() =>
        IsCommandAvailableAsync(MistralCli.Binary ?? "vibe", "--version");

    public Task<bool> IsOllamaAvailableAsync() => IsCommandAvailableAsync("ollama", "--version");

    private static async Task<bool> IsCommandAvailableAsync(string command, string arguments)
    {
        try
        {
            var (ok, _) = await RunProcessAsync(command, arguments, workingDirectory: null);
            return ok;
        }
        catch { return false; }
    }

    private static async Task<(bool Ok, string Output)> RunProcessAsync(
        string file, string args, string? workingDirectory)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(file, args, workingDirectory, TimeSpan.FromSeconds(10));
            return (res.Success, res.Stdout + res.Stderr);
        }
        catch { return (false, ""); }
    }

    public enum GitInitResult { NotAttempted, AlreadyExists, Created, GitMissing, Failed }

    public enum GitWorkspaceKind { DirectoryMissing, Eligible, Repository, MetadataPresent, GitUnavailable }

    public sealed record GitWorkspaceInspection(GitWorkspaceKind Kind, string WorkspacePath, string? RepositoryRoot)
    {
        public bool CanInitialize => Kind == GitWorkspaceKind.Eligible;
    }

    public sealed record InitializeResult(List<string> Written, List<string> Skipped, GitInitResult GitInit);
}
