namespace KittyClaw.Core.Services;

/// <summary>Reads automatic agent context without following workspace-controlled links.</summary>
public static class AgentContextFiles
{
    public static string RequirePath(string workspace, string path)
    {
        // The configured workspace is the trusted root. Check every descendant, including
        // missing leaf paths, so an existing linked parent cannot redirect a later write.
        var root = Path.GetFullPath(workspace);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(relative) || parts.Length == 0 || !parts[0].Equals(".agents", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || parts.Any(part => part is "." or ".."))
            throw new InvalidOperationException("Agent context must stay inside the workspace's .agents directory.");

        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Agent context cannot use linked files or directories.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return fullPath;
    }

    public static async Task<string?> ReadOptionalTextAsync(
        string workspace, string path, CancellationToken cancellationToken = default)
    {
        var validated = RequirePath(workspace, path);
        return File.Exists(validated) ? await File.ReadAllTextAsync(validated, cancellationToken) : null;
    }
}
