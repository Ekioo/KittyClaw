using System.Text;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed record MemoryCapitalizationResult(
    MemoryCapitalizationStatus Status, int Added, string? Error = null);

/// <summary>Native, idempotent persistence of reusable lessons owned by a column processor.</summary>
public class ColumnMemoryCapitalizationService(ProjectService projects, DurableWriteRouter? durableWrites = null)
{
    internal const int MaximumLessons = 50;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public async Task<MemoryCapitalizationResult> CapitalizeAsync(
        string projectSlug, int columnId, string checkpointId, IEnumerable<string>? lessons,
        CancellationToken cancellationToken = default, int? ticketId = null)
    {
        var normalized = (lessons ?? [])
            .Select(Normalize).Where(x => x.Length >= 12)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var project = await projects.GetProjectAsync(projectSlug);
        if (project is null) return new(MemoryCapitalizationStatus.Failed, 0, $"Project '{projectSlug}' not found.");
        var relativeMemoryDir = Path.Combine(".agents", "processors", $"column-{columnId}", "memory");
        var root = projects.ResolveWorkspacePath(project);
        await ProcessorMemoryGit.CommitAsync(root, columnId, cancellationToken);
        DurableWriteRoute? route = null;
        if (durableWrites is not null && project.WorktreesEnabled)
        {
            route = await durableWrites.ResolveAsync(projectSlug, ticketId, [relativeMemoryDir], cancellationToken);
            root = route.RootPath;
        }
        var memoryDir = Path.Combine(root, ".agents", "processors",
            $"column-{columnId}", "memory");
        var topicPath = Path.Combine(memoryDir, "pipeline-lessons.md");
        var indexPath = Path.Combine(memoryDir, "MEMORY.md");
        var marker = $"<!-- checkpoint:{checkpointId} -->";
        var routeFinalized = false;

        async Task<MemoryCapitalizationResult> PersistAsync(MemoryCapitalizationStatus status, int added)
        {
            if (route?.Kind == DurableWriteKind.Maintenance && durableWrites is not null)
            {
                routeFinalized = true;
                var validation = await durableWrites.CommitAndQueueAsync(projectSlug, route,
                    $"chore(memory): capitalize column {columnId} lessons", cancellationToken);
                if (validation.Status != DurableWriteValidationStatus.Ready)
                    return new(MemoryCapitalizationStatus.Failed, 0,
                        validation.Error ?? "Durable memory changes require review before integration.");
            }
            else await ProcessorMemoryGit.CommitAsync(root, columnId, cancellationToken);
            return new(status, added);
        }

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            if (normalized.Count == 0)
                return await PersistAsync(MemoryCapitalizationStatus.NoChange, 0);
            Directory.CreateDirectory(memoryDir);
            var existing = File.Exists(topicPath) ? await File.ReadAllTextAsync(topicPath, cancellationToken) : "";
            if (existing.Contains(marker, StringComparison.Ordinal))
            {
                // The topic is the durable journal. A process may have stopped after replacing it
                // but before replacing the injectable index, so every replay repairs the derived
                // index before declaring the checkpoint complete.
                await WriteIndexAsync(indexPath, ParseEntries(existing), cancellationToken);
                return await PersistAsync(MemoryCapitalizationStatus.Succeeded, 0);
            }

            var entries = ParseEntries(existing);
            var known = entries.Select(e => e.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = normalized.Where(known.Add).Select(text => new Lesson(checkpointId, text)).ToList();
            if (added.Count == 0)
                return await PersistAsync(MemoryCapitalizationStatus.NoChange, 0);
            entries.AddRange(added);
            entries = entries.TakeLast(MaximumLessons).ToList();

            var topic = new StringBuilder("---\ntitle: Pipeline lessons\n---\n\n# Pipeline lessons\n\n");
            foreach (var entry in entries)
                topic.AppendLine($"<!-- checkpoint:{entry.Checkpoint} -->\n- {entry.Text}\n");
            await WriteAtomicallyAsync(topicPath, topic.ToString(), cancellationToken);
            await WriteIndexAsync(indexPath, entries, cancellationToken);
            return await PersistAsync(MemoryCapitalizationStatus.Succeeded, added.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(MemoryCapitalizationStatus.Failed, 0, ex.Message);
        }
        finally
        {
            WriteLock.Release();
            if (route is not null && !routeFinalized && durableWrites is not null)
                await durableWrites.PreserveExecutionAsync(projectSlug, route, "Processor memory write interrupted before commit.");
        }
    }

    private static string Normalize(string value) =>
        string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static List<Lesson> ParseEntries(string content)
    {
        var result = new List<Lesson>();
        string? checkpoint = null;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<!-- checkpoint:") && trimmed.EndsWith(" -->"))
                checkpoint = trimmed[16..^4];
            else if (checkpoint is not null && trimmed.StartsWith("- "))
            {
                result.Add(new(checkpoint, trimmed[2..].Trim()));
                checkpoint = null;
            }
        }
        return result;
    }

    private async Task WriteIndexAsync(
        string indexPath, IReadOnlyList<Lesson> entries, CancellationToken cancellationToken)
    {
        var index = new StringBuilder("# Processor memory\n\n");
        foreach (var entry in entries.Reverse())
            index.AppendLine($"[5] [Pipeline lesson](pipeline-lessons.md) — {entry.Text}");
        await WriteAtomicallyAsync(indexPath, index.ToString(), cancellationToken);
    }

    protected virtual async Task WriteAtomicallyAsync(
        string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record Lesson(string Checkpoint, string Text);
}
