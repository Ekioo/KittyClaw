using System.Text.Json.Serialization;

namespace KittyClaw.Core.Evidence;

/// <summary>The remediation action a consumer must take to resolve degraded evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecoveryAction
{
    /// <summary>Evidence is complete; no remediation required.</summary>
    None,
    /// <summary>Re-run the agent to capture fresh observable artifacts.</summary>
    Recapture,
    /// <summary>Inspect conflicting verified sources and correct the discrepancy before re-running.</summary>
    Reconcile,
}

/// <summary>
/// Actionable recovery guidance for a degraded <see cref="EvidenceStatus"/>.
/// Produced by <see cref="EvidenceRecoveryAdvisor"/>; surfaced in <see cref="TicketDecisionBrief"/>.
/// </summary>
public sealed record EvidenceRecoveryGuidance(
    RecoveryAction Action,
    string Reason,
    IReadOnlyList<string> AffectedRunIds
);
