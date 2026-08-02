namespace KittyClaw.Core.Models;

public enum ApprovalDecisionKind { AllowOnce, AllowForTicket, Deny }
public enum ApprovalReceiptOutcome { Allowed, Denied, Expired, FailedClosed, EffectSucceeded, EffectFailed, Unknown }

public sealed record ApprovalRequestInput(
    string RequestId, string DedupeKey, int SchemaVersion, string ActionClass, string Operation,
    string ResourceKind, string ResourceCanonicalId, string ResourceDisplay, string Reason,
    string? RiskSummary, string? ScopeRequested, int? DurationRequestedSeconds,
    string ProviderName, string ProviderCliVersion, string AdapterVersion, string EnforcementProfile,
    string RunId, int TicketId, string AgentSlug, DateTime ObservedAt, DateTime ExpiresAt,
    string? ToolName, string? RedactedArgumentsHash, string? EvidenceSource);

public sealed record ApprovalRequestRecord(
    string RequestId, string DedupeKey, int SchemaVersion, string ProjectSlug, string ActionClass,
    string Operation, string ResourceKind, string ResourceCanonicalId, string ResourceDisplay,
    string Reason, string? RiskSummary, string? ScopeRequested, int? DurationRequestedSeconds,
    string ProviderName, string ProviderCliVersion, string AdapterVersion, string EnforcementProfile,
    string RunId, int TicketId, string AgentSlug, DateTime ObservedAt, DateTime ExpiresAt,
    string? ToolName, string? RedactedArgumentsHash, string? EvidenceSource, string State);

public sealed record ApprovalDecisionInput(
    string DecisionId, string RequestId, ApprovalDecisionKind Kind, string Actor,
    DateTime CreatedAt, DateTime ExpiresAt, string Scope, string Reason, string? SupersededDecisionId);

public sealed record ApprovalDecisionRecord(
    string DecisionId, string RequestId, ApprovalDecisionKind Kind, string Actor,
    DateTime CreatedAt, DateTime ExpiresAt, string Scope, string Reason,
    string? SupersededDecisionId, DateTime? ConsumedAt);

public sealed record ApprovalReceiptInput(
    string ReceiptId, string DecisionId, string RequestId, string EnforcementPointId,
    string AdapterVersion, string EnforcementProfile, DateTime StartedAt, DateTime EndedAt,
    ApprovalReceiptOutcome Outcome, string RedactedEffectIdentity);

public sealed record ApprovalReceiptRecord(
    string ReceiptId, string DecisionId, string RequestId, string EnforcementPointId,
    string AdapterVersion, string EnforcementProfile, DateTime StartedAt, DateTime EndedAt,
    ApprovalReceiptOutcome Outcome, string RedactedEffectIdentity, string PreviousHash, string IntegrityHash);

public sealed record ApprovalRegistryQuery(string? RunId = null, int? TicketId = null, string? Provider = null);
