namespace KittyClaw.Core.Evidence;

/// <summary>
/// Derives actionable <see cref="EvidenceRecoveryGuidance"/> from a <see cref="TicketEvidence"/> bundle.
/// Each degraded status maps to a distinct, explicit action rather than a generic retry instruction.
/// Multi-run evidence is represented explicitly: AffectedRunIds lists all runs that contributed to
/// the current state, preserving source identity for diagnosis.
/// </summary>
public static class EvidenceRecoveryAdvisor
{
    /// <summary>
    /// Returns guidance appropriate for the bundle's current <see cref="TicketEvidence.Status"/>.
    /// <list type="table">
    ///   <item><term>Missing</term><description>Recapture — no observable evidence was recorded.</description></item>
    ///   <item><term>Stale</term><description>Recapture — evidence predates the freshness threshold.</description></item>
    ///   <item><term>Contradictory</term><description>Reconcile — verified sources disagree; manual inspection required before re-running.</description></item>
    ///   <item><term>Partial</term><description>Recapture — replace unverified agent claims with observable artifacts.</description></item>
    ///   <item><term>Complete</term><description>None — no recovery required.</description></item>
    /// </list>
    /// </summary>
    public static EvidenceRecoveryGuidance Advise(TicketEvidence evidence)
    {
        return evidence.Status switch
        {
            EvidenceStatus.Missing => new EvidenceRecoveryGuidance(
                RecoveryAction.Recapture,
                "No observable evidence was captured. Re-run the agent to produce verified artifacts (process exit codes, git state, test results).",
                evidence.RunIds),

            EvidenceStatus.Stale => new EvidenceRecoveryGuidance(
                RecoveryAction.Recapture,
                "Evidence predates the staleness threshold. Re-run the agent to capture fresh artifacts, or lower the freshness threshold for this context.",
                evidence.RunIds),

            EvidenceStatus.Contradictory => new EvidenceRecoveryGuidance(
                RecoveryAction.Reconcile,
                BuildContradictoryReason(evidence),
                evidence.RunIds),

            EvidenceStatus.Partial => new EvidenceRecoveryGuidance(
                RecoveryAction.Recapture,
                "One or more items carry unverified agent claims. Re-run the agent so claims are replaced by observable artifacts (process exit codes, git state).",
                evidence.RunIds),

            _ => new EvidenceRecoveryGuidance(
                RecoveryAction.None,
                "Evidence is complete and verified; no recovery action required.",
                evidence.RunIds),
        };
    }

    private static string BuildContradictoryReason(TicketEvidence evidence)
    {
        var conflicted = evidence.ChangedFiles
            .Where(f => f.Provenance.Trust == EvidenceTrust.Verified)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(f => f.Kind).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToList();

        var paths = conflicted.Count > 0 ? string.Join(", ", conflicted) : "(unknown paths)";
        return $"Verified sources disagree on the change kind for: {paths}. Inspect the conflicting captures and correct the discrepancy before re-running.";
    }
}
