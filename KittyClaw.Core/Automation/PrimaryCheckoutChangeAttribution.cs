using System.Text.Json;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Attributes a primary-repository fingerprint drift observed at the end of a ticket run.
/// Drift alone never convicts the agent: it is attributed to the agent only when the run's own
/// tool stream declares a write into the primary checkout — a write-capable file tool whose
/// absolute path resolves to a path that actually changed, a mutating <c>git</c> invocation that
/// references the primary repository, or a shell command that references a changed path. Anything
/// else is classified by the caller as a coordinated KittyClaw change (synchronization window) or
/// an external change of undetermined origin, and is reported without failing the run.
/// </summary>
internal static partial class PrimaryCheckoutChangeAttribution
{
    private static readonly string[] WriteToolNames =
    [
        "write", "edit", "multiedit", "notebookedit", "write_file", "create_file", "edit_file",
        "save_file", "apply_patch", "str_replace_editor", "str_replace_based_edit_tool",
    ];

    private static readonly string[] PathProperties = ["file_path", "path", "notebook_path", "target_file"];
    private static readonly string[] CommandProperties = ["command", "cmd", "script"];

    /// <summary>
    /// Cheap live pre-filter: does the raw tool-use detail mention the primary repository at all?
    /// JSON-escaped backslashes are tolerated by collapsing repeated slashes before comparing.
    /// </summary>
    internal static bool MentionsPrimaryRepository(string? detail, string primaryRepositoryPath) =>
        !string.IsNullOrEmpty(detail) &&
        NormalizeForSearch(detail).Contains(NormalizeRoot(primaryRepositoryPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Full evidence check for one tool-use event, cross-referenced against the paths that
    /// actually changed. Read-only references to the primary repository are not evidence.
    /// </summary>
    internal static bool IsAgentWriteEvidence(
        string toolName, string? detail, string primaryRepositoryPath, IReadOnlyCollection<string> changedPaths)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var root = NormalizeRoot(primaryRepositoryPath);

        string? pathValue = null;
        string? commandValue = null;
        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in PathProperties)
                    if (document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        pathValue = value.GetString();
                        break;
                    }
                foreach (var property in CommandProperties)
                    if (document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        commandValue = value.GetString();
                        break;
                    }
            }
        }
        catch (JsonException)
        {
            // Provider adapters may surface a bare command string instead of structured input.
            commandValue = detail;
        }

        if (pathValue is not null && IsWriteTool(toolName) && Path.IsPathRooted(pathValue))
        {
            var normalized = NormalizeForSearch(Path.GetFullPath(pathValue)).TrimEnd('/');
            if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith(root + "/.agents/channel/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized[(root.Length + 1)..];
                // A declared write whose target did not actually change (denied, failed, …)
                // must not absorb a drift that came from somewhere else.
                if (changedPaths.Count == 0
                    || changedPaths.Any(p => string.Equals(NormalizeForSearch(p), relative, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        if (commandValue is not null)
        {
            var command = NormalizeForSearch(commandValue);
            if (command.Contains(root, StringComparison.OrdinalIgnoreCase))
            {
                if (MutatingGitRegex().IsMatch(command)) return true;
                foreach (var changed in changedPaths)
                    if (command.Contains($"{root}/{NormalizeForSearch(changed)}", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Best-effort list of repository-relative paths whose state differs between two fingerprints
    /// produced by <c>AgentRunner.CaptureRepositoryStateAsync</c> (format: status, NUL, diff, then
    /// one NUL-separated <c>path:HASH</c> entry per untracked file).
    /// </summary>
    internal static IReadOnlyList<string> ChangedPaths(string beforeFingerprint, string afterFingerprint)
    {
        var before = ParseFingerprint(beforeFingerprint);
        var after = ParseFingerprint(afterFingerprint);
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in SymmetricDifference(before.StatusLines, after.StatusLines))
            foreach (var path in StatusLinePaths(line))
                paths.Add(path);

        foreach (var (path, hash) in before.UntrackedHashes)
            if (!after.UntrackedHashes.TryGetValue(path, out var other) || !string.Equals(hash, other, StringComparison.Ordinal))
                paths.Add(path);
        foreach (var path in after.UntrackedHashes.Keys)
            if (!before.UntrackedHashes.ContainsKey(path))
                paths.Add(path);

        var beforeDiff = DiffBlocks(before.Diff);
        var afterDiff = DiffBlocks(after.Diff);
        foreach (var (path, block) in beforeDiff)
            if (!afterDiff.TryGetValue(path, out var other) || !string.Equals(block, other, StringComparison.Ordinal))
                paths.Add(path);
        foreach (var path in afterDiff.Keys)
            if (!beforeDiff.ContainsKey(path))
                paths.Add(path);

        return paths.ToList();
    }

    private static bool IsWriteTool(string toolName) =>
        WriteToolNames.Contains(toolName.Trim().ToLowerInvariant());

    private static string NormalizeRoot(string primaryRepositoryPath) =>
        NormalizeForSearch(Path.GetFullPath(primaryRepositoryPath)).TrimEnd('/');

    private static string NormalizeForSearch(string value) =>
        CollapseSlashesRegex().Replace(value.Replace('\\', '/'), "/");

    private sealed record FingerprintParts(
        HashSet<string> StatusLines, string Diff, Dictionary<string, string> UntrackedHashes);

    private static FingerprintParts ParseFingerprint(string fingerprint)
    {
        var segments = fingerprint.Split('\0');
        var status = segments.Length > 0 ? segments[0] : "";
        var diff = segments.Length > 1 ? segments[1] : "";
        var untracked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in segments.Skip(2))
        {
            var separator = entry.LastIndexOf(':');
            if (separator > 0) untracked[entry[..separator]] = entry[(separator + 1)..];
        }
        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return new(lines, diff, untracked);
    }

    private static HashSet<string> SymmetricDifference(HashSet<string> left, HashSet<string> right)
    {
        var result = new HashSet<string>(left, StringComparer.Ordinal);
        result.SymmetricExceptWith(right);
        return result;
    }

    private static IEnumerable<string> StatusLinePaths(string statusLine)
    {
        // Porcelain v1: "XY path" or "XY orig -> renamed"; special characters are C-quoted.
        if (statusLine.Length <= 3) yield break;
        var payload = statusLine[3..];
        foreach (var part in payload.Split(" -> ", StringSplitOptions.RemoveEmptyEntries))
            yield return part.Trim().Trim('"');
    }

    private static Dictionary<string, string> DiffBlocks(string diff)
    {
        // Catches content changes that keep an identical porcelain status letter, e.g. a locally
        // modified file whose HEAD-side blob moved because the checkout was fast-forwarded.
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in diff.Split("diff --git ", StringSplitOptions.RemoveEmptyEntries))
        {
            var header = block.Split('\n', 2)[0];
            var marker = header.LastIndexOf(" b/", StringComparison.Ordinal);
            if (marker < 0) continue;
            blocks[header[(marker + 3)..].Trim().Trim('"')] = block;
        }
        return blocks;
    }

    [GeneratedRegex(
        @"\bgit\b[^\n|;&]*\b(add|am|apply|checkout|cherry-pick|clean|commit|merge|mv|pull|rebase|reset|restore|revert|rm|stash|switch|update-ref|worktree)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MutatingGitRegex();

    [GeneratedRegex("/{2,}")]
    private static partial Regex CollapseSlashesRegex();
}
