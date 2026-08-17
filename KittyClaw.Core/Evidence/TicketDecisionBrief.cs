using System.Text.Json.Serialization;

namespace KittyClaw.Core.Evidence;

/// <summary>Ship / Fix / Block verdict derived from the structured evidence contract.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DecisionVerdict
{
    /// <summary>Evidence is complete and all verifiable checks passed; suitable for QA hand-off.</summary>
    Ship,
    /// <summary>Evidence is present but failures or unverified claims require remediation.</summary>
    Fix,
    /// <summary>Evidence is absent, stale, or contradictory; a decision cannot be made.</summary>
    Block,
}

/// <summary>A single structured finding derived from evidence items.</summary>
public sealed record DecisionFinding(
    string Category,
    string Summary,
    bool IsBlocking
);

/// <summary>
/// Provider-neutral decision brief synthesised from a <see cref="TicketEvidence"/> bundle.
/// All fields are evidence-backed; no transcript parsing is involved.
/// </summary>
public sealed class TicketDecisionBrief
{
    public required string TicketId { get; init; }
    public required string ProjectSlug { get; init; }
    /// <summary>UTC timestamp copied from <see cref="TicketEvidence.CapturedAt"/>.</summary>
    public required DateTime CapturedAt { get; init; }
    public required DecisionVerdict Verdict { get; init; }
    public required EvidenceStatus EvidenceStatus { get; init; }
    public required int FilesChanged { get; init; }
    public required List<ChangedFile> ChangedFiles { get; init; }
    public required int CommandsRun { get; init; }
    public required int TestsPassed { get; init; }
    public required int TestsFailed { get; init; }
    public required bool? RepositoryClean { get; init; }
    public required string? RepositoryBranch { get; init; }
    public required string? RepositoryCommitSha { get; init; }
    public required string? RepositoryBaseCommitSha { get; init; }
    public required List<string> RunIds { get; init; }
    public required List<DecisionFinding> Findings { get; init; }
    /// <summary>Actionable recovery guidance derived from the evidence status.</summary>
    public required EvidenceRecoveryGuidance RecoveryGuidance { get; init; }
}
