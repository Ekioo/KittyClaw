using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.Core.Evidence;

/// <summary>
/// Persists and retrieves evidence bundles with freshness metadata and source identity.
/// Bundles are stored below a project-scoped directory so identifiers from distinct
/// projects can never overwrite or expose one another.
/// </summary>
public sealed class EvidenceStore
{
    private readonly string _runsDir;

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Staleness threshold: evidence older than this is marked <see cref="EvidenceStatus.Stale"/>.</summary>
    public static readonly TimeSpan DefaultStalenessThreshold = TimeSpan.FromHours(24);

    public EvidenceStore(string dataDir)
    {
        _runsDir = Path.Combine(dataDir, "runs");
        Directory.CreateDirectory(_runsDir);
    }

    /// <summary>Persists a per-run evidence bundle.</summary>
    public void SaveRun(TicketEvidence evidence)
    {
        var runId = evidence.RunIds.Count > 0 ? evidence.RunIds[0] : "unknown";
        var path = RunPath(evidence.ProjectSlug, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, s_json));
    }

    /// <summary>Loads the per-run evidence bundle for <paramref name="runId"/>. Returns null if absent or corrupt.</summary>
    public TicketEvidence? LoadRun(string projectSlug, string runId)
    {
        var path = RunPath(projectSlug, runId);
        return TryLoad(path);
    }

    /// <summary>Loads the merged per-ticket evidence bundle. Returns null if absent or corrupt.</summary>
    public TicketEvidence? LoadTicket(string projectSlug, string ticketId)
    {
        var path = TicketPath(projectSlug, ticketId);
        return TryLoad(path);
    }

    /// <summary>
    /// Merges <paramref name="runEvidence"/> into the accumulated ticket bundle and persists both.
    /// Source identity is preserved: each item keeps its original <see cref="EvidenceProvenance.RunId"/>.
    /// <see cref="TicketEvidence.CapturedAt"/> is updated to the run's capture time.
    /// <see cref="TicketEvidence.Status"/> is recomputed after the merge.
    /// </summary>
    public TicketEvidence MergeAndSave(TicketEvidence runEvidence)
    {
        var existing = LoadTicket(runEvidence.ProjectSlug, runEvidence.TicketId);
        var merged = BuildMerged(existing, runEvidence);
        merged.Status = ProvenanceRules.ComputeStatus(merged);
        SaveRun(runEvidence);
        SaveTicket(merged);
        return merged;
    }

    /// <summary>Persists the merged per-ticket evidence bundle directly.</summary>
    public void SaveTicket(TicketEvidence evidence)
    {
        var path = TicketPath(evidence.ProjectSlug, evidence.TicketId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, s_json));
    }

    /// <summary>
    /// Marks the bundle's <see cref="TicketEvidence.Status"/> as <see cref="EvidenceStatus.Stale"/>
    /// when <c>now − CapturedAt &gt; threshold</c>. Returns the bundle (mutated in-place).
    /// </summary>
    public static TicketEvidence ApplyStaleness(
        TicketEvidence evidence,
        TimeSpan threshold,
        DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.UtcNow;
        if (evidence.Status != EvidenceStatus.Stale && now - evidence.CapturedAt > threshold)
            evidence.Status = EvidenceStatus.Stale;
        return evidence;
    }

    private static TicketEvidence BuildMerged(TicketEvidence? existing, TicketEvidence incoming)
    {
        var merged = new TicketEvidence
        {
            TicketId = incoming.TicketId,
            ProjectSlug = incoming.ProjectSlug,
            CapturedAt = incoming.CapturedAt,
        };

        if (existing is not null)
        {
            merged.RunIds.AddRange(existing.RunIds);
            merged.CommandsRun.AddRange(existing.CommandsRun);
            merged.Retries.AddRange(existing.Retries);
            if (existing.RepositoryState is not null)
                merged.RepositoryState = existing.RepositoryState;
        }

        // Merge incoming run — source identity (Provenance.RunId) is preserved on each item.
        foreach (var runId in incoming.RunIds)
            if (!merged.RunIds.Contains(runId, StringComparer.Ordinal))
                merged.RunIds.Add(runId);

        // Changed files are a repository snapshot, not an event log. The latest stable
        // base-to-commit diff replaces transient or stale lists from earlier runs.
        merged.ChangedFiles.AddRange(incoming.ChangedFiles);
        merged.CommandsRun.AddRange(incoming.CommandsRun);
        merged.Retries.AddRange(incoming.Retries);

        // Latest run's git state is the most current snapshot — replace the prior one.
        if (incoming.RepositoryState is not null)
            merged.RepositoryState = incoming.RepositoryState;

        return merged;
    }

    private TicketEvidence? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<TicketEvidence>(File.ReadAllText(path), s_json);
        }
        catch { return null; }
    }

    private string RunPath(string projectSlug, string runId) =>
        Path.Combine(ProjectDirectory(projectSlug), $"run-{SafeSegment(runId)}.evidence.json");

    private string TicketPath(string projectSlug, string ticketId) =>
        Path.Combine(ProjectDirectory(projectSlug), $"ticket-{SafeSegment(ticketId)}.evidence.json");

    private string ProjectDirectory(string projectSlug) =>
        Path.Combine(_runsDir, "evidence", SafeSegment(projectSlug));

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c));
    }
}
