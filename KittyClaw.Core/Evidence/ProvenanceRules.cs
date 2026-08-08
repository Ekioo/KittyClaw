namespace KittyClaw.Core.Evidence;

/// <summary>
/// Enforces provenance rules and computes the aggregate <see cref="EvidenceStatus"/>
/// for a <see cref="TicketEvidence"/> bundle.
/// </summary>
public static class ProvenanceRules
{
    private static readonly HashSet<string> s_verifiedSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "process-exit", "filesystem", "test-runner",
    };

    private static readonly HashSet<string> s_knownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "codex", "grok", "mistral", "unknown",
    };

    /// <summary>True when the source string qualifies as an observable (Verified-grade) artifact.</summary>
    public static bool IsVerifiedSource(string source) => s_verifiedSources.Contains(source);

    /// <summary>True when the provider string is a recognized CLI provider name.</summary>
    public static bool IsKnownProvider(string provider) => s_knownProviders.Contains(provider);

    /// <summary>
    /// Computes the aggregate <see cref="EvidenceStatus"/> for a bundle.
    ///
    /// Priority order:
    /// 1. <see cref="EvidenceStatus.Contradictory"/> — two Verified items disagree on the same file's change kind.
    /// 2. <see cref="EvidenceStatus.Missing"/> — any item carries Trust=Missing, or the bundle has no items.
    /// 3. <see cref="EvidenceStatus.Partial"/> — any item carries Trust=AgentClaim (but none Missing).
    /// 4. <see cref="EvidenceStatus.Complete"/> — all items carry Trust=Verified and at least one is present.
    ///
    /// <see cref="EvidenceStatus.Stale"/> is not computed here; consumers set it based on freshness
    /// relative to the run's completion time or a configurable threshold.
    /// </summary>
    public static EvidenceStatus ComputeStatus(TicketEvidence evidence)
    {
        var all = AllProvenances(evidence).ToList();

        if (all.Count == 0)
            return EvidenceStatus.Missing;

        if (HasVerifiedContradiction(evidence))
            return EvidenceStatus.Contradictory;

        if (all.Any(p => p.Trust == EvidenceTrust.Missing))
            return EvidenceStatus.Missing;

        if (all.Any(p => p.Trust == EvidenceTrust.AgentClaim))
            return EvidenceStatus.Partial;

        return EvidenceStatus.Complete;
    }

    private static bool HasVerifiedContradiction(TicketEvidence evidence)
    {
        return evidence.ChangedFiles
            .Where(f => f.Provenance.Trust == EvidenceTrust.Verified)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Select(f => f.Kind).Distinct().Count() > 1);
    }

    private static IEnumerable<EvidenceProvenance> AllProvenances(TicketEvidence evidence)
    {
        foreach (var f in evidence.ChangedFiles) yield return f.Provenance;
        foreach (var c in evidence.CommandsRun) yield return c.Provenance;
        if (evidence.RepositoryState is not null) yield return evidence.RepositoryState.Provenance;
        foreach (var r in evidence.Retries) yield return r.Provenance;
    }
}
