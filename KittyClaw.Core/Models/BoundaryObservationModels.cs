namespace KittyClaw.Core.Models;

public enum BoundaryActionClass
{
    PushOrPullRequest,
    PublishOrDeploy,
    NewNetworkDestination,
    SecretAccess,
    DestructiveOperation,
}

public sealed record BoundaryObservation(
    string ObservationId,
    string ProjectSlug,
    string RunId,
    int? TicketId,
    string AgentSlug,
    string ProviderName,
    DateTime ObservedAt,
    string ToolName,
    string ArgumentsHash,
    BoundaryActionClass Classification,
    string ResourceKind,
    string ResourceDisplay,
    string Outcome,
    bool? IsFalsePositive);

public sealed record BoundaryObservationMetrics(
    int RunCount,
    int PotentialRequestCount,
    int ReviewedCount,
    int FalsePositiveCount,
    double PotentialRequestsPerTwentyRuns,
    double? ReviewedFalsePositiveRate,
    bool MeetsOrdinaryRunTarget);
