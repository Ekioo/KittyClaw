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
        DurableWriteRoute? route = null;
        var routeFinalized = false;
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
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

            if (normalized.Count == 0 && !File.Exists(topicPath))
                return await PersistAsync(MemoryCapitalizationStatus.NoChange, 0);
            Directory.CreateDirectory(memoryDir);
            var canonical = File.Exists(indexPath) ? await File.ReadAllTextAsync(indexPath, cancellationToken) : "";
            var legacy = File.Exists(topicPath) ? await File.ReadAllTextAsync(topicPath, cancellationToken) : "";
            var entries = ParseEntries(canonical);
            var known = entries.Select(e => e.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries.AddRange(ParseEntries(legacy).Where(e => known.Add(e.Text)));
            var added = normalized.Where(known.Add).Select(text => new Lesson(checkpointId, text)).ToList();
            entries.AddRange(added);
            entries = entries.TakeLast(MaximumLessons).ToList();

            // Preserve handwritten memory; replace only the generated section and the old
            // derived index rows. One atomic canonical file removes the two-file crash gap.
            const string section = "<!-- kittyclaw:pipeline-lessons -->";
            const string sectionEnd = "<!-- /kittyclaw:pipeline-lessons -->";
            var sectionStart = canonical.IndexOf(section, StringComparison.Ordinal);
            var end = canonical.IndexOf(sectionEnd, StringComparison.Ordinal);
            var suffix = end >= 0 ? canonical[(end + sectionEnd.Length)..] : "";
            var custom = sectionStart >= 0 ? canonical[..sectionStart] : canonical;
            custom = string.Join("\n", custom.Split('\n').Where(line =>
                !line.StartsWith("[5] [Pipeline lesson](pipeline-lessons.md) — ", StringComparison.Ordinal)));
            if (string.IsNullOrWhiteSpace(custom)) custom = "# Processor memory\n";
            var content = new StringBuilder(custom.TrimEnd() + "\n\n" + section + "\n\n## Pipeline lessons\n\n");
            foreach (var entry in entries)
                content.AppendLine($"<!-- checkpoint:{entry.Checkpoint} -->\n- {entry.Text}\n");
            content.Append(sectionEnd).Append(suffix.Length > 0 ? suffix : "\n");
            await WriteAtomicallyAsync(indexPath, content.ToString(), cancellationToken);
            // Delete only the known generated legacy file after the canonical replacement.
            // Unknown handwritten content remains available for manual review.
            if (IsGeneratedLegacy(legacy)) File.Delete(topicPath);
            var status = added.Count > 0 || canonical.Contains(marker, StringComparison.Ordinal) || legacy.Contains(marker, StringComparison.Ordinal)
                ? MemoryCapitalizationStatus.Succeeded : MemoryCapitalizationStatus.NoChange;
            return await PersistAsync(status, added.Count);
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

    private static bool IsGeneratedLegacy(string content)
    {
        if (content.Length == 0) return false;
        var expected = new StringBuilder("---\ntitle: Pipeline lessons\n---\n\n# Pipeline lessons\n\n");
        foreach (var entry in ParseEntries(content))
            expected.AppendLine($"<!-- checkpoint:{entry.Checkpoint} -->\n- {entry.Text}\n");
        static string CanonicalLines(string text) => string.Join("\n", text.Split('\n').Select(line => line.TrimEnd())).Trim();
        return CanonicalLines(content) == CanonicalLines(expected.ToString());
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
