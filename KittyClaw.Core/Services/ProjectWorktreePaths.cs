using System.Security.Cryptography;
using System.Text;

namespace KittyClaw.Core.Services;

/// <summary>Keeps generated worktree names short enough for Git's administrative paths.</summary>
internal static class ProjectWorktreePaths
{
    internal const int MaxLeafLength = 40;

    internal static string LeafName(string kind, string identifier)
    {
        var name = $"{kind}-{identifier}";
        if (name.Length <= MaxLeafLength) return name;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..12];
        return $"{name[..(MaxLeafLength - hash.Length - 1)]}-{hash}";
    }

    internal static string GetPath(string repository, string kind, string identifier, bool legacy = false)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        var parent = Directory.GetParent(root)?.FullName
            ?? throw new InvalidOperationException($"Repository '{repository}' has no parent directory.");
        var leaf = legacy ? $"{kind}-{identifier}" : LeafName(kind, identifier);
        return Path.GetFullPath(Path.Combine(parent, $"{Path.GetFileName(root)}.worktrees", leaf));
    }
}
