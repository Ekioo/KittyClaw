using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public enum RuntimeBoundaryDisposition { Pending, Allowed, Denied, FailedClosed, EffectSucceeded, EffectFailed }

public sealed record RuntimeBoundaryAttempt(
    string RequestId, string DedupeKey, string ProjectSlug, string RunId, int TicketId,
    string AgentSlug, string Provider, string ProviderVersion, BoundaryActionClass ActionClass,
    string Operation, string ResourceKind, string ResourceCanonicalId, string ResourceDisplay,
    string Reason, string ArgumentsHash, DateTime AttemptedAt, DateTime ExpiresAt);

public sealed record RuntimeBoundaryResult(RuntimeBoundaryDisposition Disposition, string RequestId, string? DecisionId = null);

/// <summary>
/// The only advertised protected boundary. Runtime adapters must obtain an Allowed authorization
/// from this broker before invoking an effect (pre-effect hooks call <see cref="AuthorizeAsync"/>;
/// in-process effects use <see cref="ExecuteAsync"/>). Events observed from provider output are
/// deliberately excluded because they are late. Every failure path resolves to a closed result.
/// </summary>
public sealed class RuntimeBoundaryEnforcementService(ApprovalRegistryService registry)
{
    /// <summary>Registers (idempotently) and evaluates the attempt against the latest decision,
    /// persisting the matching receipt. Allowed means the caller may run exactly this effect;
    /// allow-once consumption happens atomically inside this call.</summary>
    public async Task<RuntimeBoundaryResult> AuthorizeAsync(RuntimeBoundaryAttempt attempt)
    {
        try
        {
            var request = await registry.RegisterRequestAsync(attempt.ProjectSlug, new ApprovalRequestInput(
                attempt.RequestId, attempt.DedupeKey, 1, attempt.ActionClass.ToString(), attempt.Operation,
                attempt.ResourceKind, attempt.ResourceCanonicalId, attempt.ResourceDisplay, attempt.Reason, null,
                "temporary", null, attempt.Provider, attempt.ProviderVersion, "runtime-broker-v1", "fail-closed",
                attempt.RunId, attempt.TicketId, attempt.AgentSlug, attempt.AttemptedAt, attempt.ExpiresAt,
                "runtime-broker", attempt.ArgumentsHash, "pre-effect-adapter"));

            var decision = ActiveDecision(await registry.QueryDecisionsAsync(attempt.ProjectSlug, request.RequestId))
                ?? await registry.FindActiveTicketGrantAsync(attempt.ProjectSlug, attempt.TicketId,
                    attempt.ActionClass.ToString(), attempt.ResourceCanonicalId, DateTime.UtcNow);
            if (decision is null) return new(RuntimeBoundaryDisposition.Pending, request.RequestId);

            var now = DateTime.UtcNow;
            if (decision.Kind == ApprovalDecisionKind.Deny || decision.ExpiresAt <= now)
            {
                await registry.AddReceiptAsync(attempt.ProjectSlug, Receipt(attempt, decision,
                    decision.Kind == ApprovalDecisionKind.Deny
                        ? ApprovalReceiptOutcome.Denied : ApprovalReceiptOutcome.Expired, now));
                return new(RuntimeBoundaryDisposition.Denied, request.RequestId, decision.DecisionId);
            }

            var receipt = Receipt(attempt, decision, ApprovalReceiptOutcome.Allowed, now);
            if (decision.Kind == ApprovalDecisionKind.AllowOnce)
                await registry.ConsumeOnceAsync(attempt.ProjectSlug, receipt);
            else
            {
                if (!decision.Scope.Equals($"ticket:{attempt.TicketId}", StringComparison.Ordinal))
                    return new(RuntimeBoundaryDisposition.Denied, request.RequestId, decision.DecisionId);
                await registry.AddReceiptAsync(attempt.ProjectSlug, receipt);
            }
            return new(RuntimeBoundaryDisposition.Allowed, request.RequestId, decision.DecisionId);
        }
        catch
        {
            return new(RuntimeBoundaryDisposition.FailedClosed, attempt.RequestId);
        }
    }

    public async Task<RuntimeBoundaryResult> ExecuteAsync(
        RuntimeBoundaryAttempt attempt, Func<CancellationToken, Task> effect, CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(attempt);
        if (authorization.Disposition != RuntimeBoundaryDisposition.Allowed) return authorization;
        try
        {
            await effect(cancellationToken);
            return authorization with { Disposition = RuntimeBoundaryDisposition.EffectSucceeded };
        }
        catch
        {
            return authorization with { Disposition = RuntimeBoundaryDisposition.EffectFailed };
        }
    }

    /// <summary>Persists the terminal effect outcome for an attempt whose effect ran outside this
    /// process (pre-effect hook path). No-op when the attempt has no decision yet.</summary>
    public async Task RecordEffectOutcomeAsync(RuntimeBoundaryAttempt attempt, bool succeeded)
    {
        var decision = ActiveDecision(await registry.QueryDecisionsAsync(attempt.ProjectSlug, attempt.RequestId))
            ?? await registry.FindActiveTicketGrantAsync(attempt.ProjectSlug, attempt.TicketId,
                attempt.ActionClass.ToString(), attempt.ResourceCanonicalId, DateTime.UtcNow);
        if (decision is null) return;
        await registry.AddReceiptAsync(attempt.ProjectSlug, Receipt(attempt, decision,
            succeeded ? ApprovalReceiptOutcome.EffectSucceeded : ApprovalReceiptOutcome.EffectFailed, DateTime.UtcNow));
    }

    /// <summary>The registry guarantees at most one non-superseded decision per request; a decision
    /// is active when no later decision points back at it through SupersededDecisionId.</summary>
    private static ApprovalDecisionRecord? ActiveDecision(IReadOnlyList<ApprovalDecisionRecord> decisions) =>
        decisions
            .Where(d => decisions.All(x => x.SupersededDecisionId != d.DecisionId))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();

    private static ApprovalReceiptInput Receipt(RuntimeBoundaryAttempt attempt, ApprovalDecisionRecord decision,
        ApprovalReceiptOutcome outcome, DateTime now) => new(
        Guid.NewGuid().ToString("N"), decision.DecisionId, attempt.RequestId,
        $"{attempt.Provider}:{attempt.ActionClass}", "runtime-broker-v1", "fail-closed",
        now, now, outcome, attempt.ResourceCanonicalId);
}
