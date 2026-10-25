using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Services;

public sealed record TemporaryFolderUsage(string Path, long Bytes, long FileCount, bool Exists, bool IsPartial);
public sealed record ProjectTemporaryStorage(string Slug, string Name, IReadOnlyList<TemporaryFolderUsage> Folders)
{
    public long Bytes => Folders.Sum(f => f.Bytes);
    public long FileCount => Folders.Sum(f => f.FileCount);
    public bool IsPartial => Folders.Any(f => f.IsPartial);
}

/// <summary>Read-only, on-demand accounting of configured and historically recorded working folders.</summary>
public sealed class TemporaryStorageService(ProjectService projects, AgentRunRegistry runs)
{
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<IReadOnlyList<ProjectTemporaryStorage>> ScanAsync(CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct);
        try
        {
            var registered = await projects.ListProjectsAsync();
            return await Task.Run<IReadOnlyList<ProjectTemporaryStorage>>(() =>
            {
                var result = new List<ProjectTemporaryStorage>();
                foreach (var project in registered)
                {
                    ct.ThrowIfCancellationRequested();
                    var roots = new List<string>
                    {
                        projects.ResolveWorkingFilesRoot(project),
                        Path.Combine(projects.DataDir, "artifacts", project.Slug)
                    };
                    foreach (var run in runs.AllForProject(project.Slug))
                        if (!string.IsNullOrWhiteSpace(run.WorkingFilesPath) &&
                            Path.IsPathFullyQualified(run.WorkingFilesPath) &&
                            Path.GetDirectoryName(run.WorkingFilesPath) is { } parent)
                            roots.Add(parent);
                    // A nested historical root is already included in its containing root.
                    var normalized = roots.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                        .OrderBy(p => p.Length).ToList();
                    var selected = new List<string>();
                    foreach (var root in normalized)
                        if (!selected.Any(p => root.StartsWith(p + Path.DirectorySeparatorChar, PathComparison)))
                            selected.Add(root);
                    result.Add(new(project.Slug, project.Name, selected.Select(p => Measure(p, ct)).ToList()));
                }
                return result.OrderByDescending(p => p.Bytes).ThenBy(p => p.Name).ToList();
            }, ct);
        }
        finally { _scanGate.Release(); }
    }

    internal static TemporaryFolderUsage Measure(string path, CancellationToken ct = default)
    {
        long bytes = 0, files = 0;
        var partial = false;
        var exists = false;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(directory.FullName);
                exists = true;
                // Never follow links/junctions outside the measured tree or into a cycle.
                if ((attributes & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                        if (entry is DirectoryInfo child) pending.Push(child);
                        else if (entry is FileInfo file) { bytes += file.Length; files++; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
                }
            }
            catch (DirectoryNotFoundException) { if (exists) partial = true; }
            catch (FileNotFoundException) { if (exists) partial = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
        }
        return new(path, bytes, files, exists, partial);
    }
}
