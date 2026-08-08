using System.Text.Json.Serialization;

namespace KittyClaw.Core.Evidence;

/// <summary>
/// Trustworthiness level of a single evidence item.
/// Provenance rule: an AgentClaim cannot be promoted to Verified without a new observable capture.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceTrust
{
    /// <summary>Source is an observable artifact: git output, process exit code, filesystem state.</summary>
    Verified,
    /// <summary>Source is free-form model text; cannot stand alone as verified evidence.</summary>
    AgentClaim,
    /// <summary>Item was expected but could not be captured.</summary>
    Missing,
}

/// <summary>
/// Aggregate status of an evidence bundle.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceStatus
{
    /// <summary>All items carry Verified provenance and required fields are present.</summary>
    Complete,
    /// <summary>Some items present but at least one carries AgentClaim provenance.</summary>
    Partial,
    /// <summary>Required items absent or all items carry Missing trust.</summary>
    Missing,
    /// <summary>Two Verified items from different sources disagree on the same observable fact.</summary>
    Contradictory,
    /// <summary>Evidence predates the current run or exceeds a consumer-defined freshness threshold.</summary>
    Stale,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileChangeKind { Added, Modified, Deleted, Renamed }

/// <summary>
/// Source identity and trustworthiness for one evidence item.
///
/// Provenance rules:
/// - Every evidence item MUST carry a non-null Provenance.
/// - <see cref="Timestamp"/> MUST be UTC.
/// - <see cref="Trust"/> = <see cref="EvidenceTrust.Verified"/> only when <see cref="Source"/> is an
///   observable artifact ("git", "process-exit", "filesystem", "test-runner").
/// - <see cref="Trust"/> = <see cref="EvidenceTrust.AgentClaim"/> when data originates from
///   free-form model output that cannot be independently confirmed.
/// - <see cref="Trust"/> = <see cref="EvidenceTrust.Missing"/> when the item was expected but
///   not captured; consumers must surface this explicitly rather than silently omitting the item.
/// - <see cref="Provider"/> MUST be a lowercase CLI provider name ("claude", "codex", "grok",
///   "mistral") or "unknown"; never embed a model name here.
/// - <see cref="RunId"/> links the item back to the AgentRun that produced it, enabling
///   multi-run evidence merging without losing origin identity.
/// </summary>
public sealed record EvidenceProvenance(
    string Source,
    string Provider,
    string RunId,
    DateTime Timestamp,
    EvidenceTrust Trust
);

/// <summary>A source file that was changed during implementation.</summary>
public sealed record ChangedFile(
    string Path,
    FileChangeKind Kind,
    string? PatchExcerpt,
    EvidenceProvenance Provenance
);

/// <summary>A shell command or test runner invocation with its observable outcome.</summary>
public sealed record CommandRecord(
    string Command,
    string? WorkingDirectory,
    int? ExitCode,
    bool Success,
    string? OutputSummary,
    int? TestsPassed,
    int? TestsFailed,
    EvidenceProvenance Provenance
);

/// <summary>A snapshot of the repository's observable state at evidence capture time.</summary>
public sealed record RepositoryState(
    string? Branch,
    string? CommitSha,
    bool? IsClean,
    List<string> UntrackedFiles,
    EvidenceProvenance Provenance
);

/// <summary>One failed attempt before the successful (or final) run.</summary>
public sealed record RetryRecord(
    int AttemptNumber,
    string? FailureReason,
    EvidenceProvenance Provenance
);

/// <summary>
/// Root evidence bundle for an implementation ticket.
/// Carries all provenance-bearing items produced across one or more agent runs.
/// Multiple runs can contribute items; each item's Provenance.RunId preserves origin identity.
/// </summary>
public sealed class TicketEvidence
{
    public required string TicketId { get; init; }
    public required string ProjectSlug { get; init; }

    /// <summary>UTC timestamp when this bundle was assembled. Used by consumers to detect Stale evidence.</summary>
    public required DateTime CapturedAt { get; init; }

    /// <summary>Aggregate status; recompute with <see cref="ProvenanceRules.ComputeStatus"/> after mutations.</summary>
    public EvidenceStatus Status { get; set; }

    /// <summary>All run IDs that contributed evidence to this bundle, in chronological order.</summary>
    public List<string> RunIds { get; init; } = [];

    public List<ChangedFile> ChangedFiles { get; init; } = [];
    public List<CommandRecord> CommandsRun { get; init; } = [];
    public RepositoryState? RepositoryState { get; set; }
    public List<RetryRecord> Retries { get; init; } = [];
}
