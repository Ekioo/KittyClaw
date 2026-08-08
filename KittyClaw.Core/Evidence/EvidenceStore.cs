using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.Core.Evidence;

/// <summary>
/// Persists and retrieves evidence bundles with freshness metadata and source identity.
/// Per-run bundles are stored at <c>&lt;dataDir&gt;/runs/&lt;runId&gt;.evidence.json</c>.
/// Per-ticket merged bundles are stored at <c>&lt;dataDir&gt;/runs/ticket-&lt;ticketId&gt;.evidence.json</c>.
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
        var path = Path.Combine(_runsDir, $"{runId}.evidence.json");
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, s_json));
    }

    /// <summary>Loads the per-run evidence bundle for <paramref name="runId"/>. Returns null if absent or corrupt.</summary>
    public TicketEvidence? LoadRun(string runId)
    {
        var path = Path.Combine(_runsDir, $"{runId}.evidence.json");
        return TryLoad(path);
    }

    /// <summary>Loads the merged per-ticket evidence bundle. Returns null if absent or corrupt.</summary>
    public TicketEvidence? LoadTicket(string ticketId)
    {
        var path = TicketPath(ticketId);
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
        var existing = LoadTicket(runEvidence.TicketId);
        var merged = BuildMerged(existing, runEvidence);
        merged.Status = ProvenanceRules.ComputeStatus(merged);
        SaveRun(runEvidence);
        SaveTicket(merged);
        return merged;
    }

    /// <summary>Persists the merged per-ticket evidence bundle directly.</summary>
    public void SaveTicket(TicketEvidence evidence)
    {
        var path = TicketPath(evidence.TicketId);
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
            merged.ChangedFiles.AddRange(existing.ChangedFiles);
            merged.CommandsRun.AddRange(existing.CommandsRun);
            merged.Retries.AddRange(existing.Retries);
            if (existing.RepositoryState is not null)
                merged.RepositoryState = existing.RepositoryState;
        }

        // Merge incoming run — source identity (Provenance.RunId) is preserved on each item.
        foreach (var runId in incoming.RunIds)
            if (!merged.RunIds.Contains(runId, StringComparer.Ordinal))
                merged.RunIds.Add(runId);

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

    private string TicketPath(string ticketId) =>
        Path.Combine(_runsDir, $"ticket-{ticketId}.evidence.json");
}
