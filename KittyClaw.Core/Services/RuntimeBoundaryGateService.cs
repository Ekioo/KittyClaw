using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed record RuntimeBoundaryGateVerdict(string Decision, string? RequestId, string Reason);

/// <summary>
/// Server side of the provider-native pre-effect hook (Claude Code PreToolUse). The hook forwards
/// its raw payload here before the tool effect runs; this service classifies it, registers the
/// approval request (pausing the run while pending, preserving the #169 suspension contract),
/// evaluates the decision through the runtime broker, and answers allow/deny/pending. Every error
/// path answers deny — the boundary fails closed, never open.
/// </summary>
public sealed class RuntimeBoundaryGateService(
    RuntimeBoundaryEnforcementService broker,
    ApprovalWorkflowService workflow,
    AgentRunRegistry runs,
    BoundaryObservationService observations)
{
    internal const string AdapterVersion = "claude-pretooluse-hook-v1";
    internal static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(30);

    public async Task<RuntimeBoundaryGateVerdict> EvaluateHookAsync(
        string projectSlug, string runId, string hookPayloadJson, bool finalize)
    {
        AgentRun? run = null;
        string? requestId = null;
        try
        {
            run = runs.Get(runId);
            if (run is null || !string.Equals(run.ProjectSlug, projectSlug, StringComparison.OrdinalIgnoreCase))
                return new("deny", null, "Fail-closed: unknown run for this project.");

            using var document = JsonDocument.Parse(hookPayloadJson);
            var root = document.RootElement;
            var toolName = root.TryGetProperty("tool_name", out var tool) && tool.ValueKind == JsonValueKind.String
                ? tool.GetString() ?? "" : "";
            var toolInputJson = root.TryGetProperty("tool_input", out var input) ? input.GetRawText() : "{}";
            var isPost = root.TryGetProperty("hook_event_name", out var eventName)
                && string.Equals(eventName.GetString(), "PostToolUse", StringComparison.OrdinalIgnoreCase);

            var match = BoundaryObservationService.Classify(toolName, toolInputJson);
            if (match is null)
                return new("allow", null, "Not a protected boundary class.");
            if (match.Value.ActionClass == BoundaryActionClass.NewNetworkDestination
                && observations.IsKnownOrLocalDestination(projectSlug, match.Value.ResourceDisplay))
                return new("allow", null, "Destination already known for this project.");

            var attempt = BuildAttempt(projectSlug, run, toolName, toolInputJson, match.Value);
            requestId = attempt.RequestId;

            if (isPost)
            {
                var succeeded = !(root.TryGetProperty("tool_response", out var response)
                    && response.ValueKind == JsonValueKind.Object
                    && response.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.False);
                await broker.RecordEffectOutcomeAsync(attempt, succeeded);
                return new("allow", attempt.RequestId, "Effect outcome recorded.");
            }

            // Register through the workflow first so a pending request pauses the provider process
            // (#169 contract) and the audit record carries the real tool name and arguments hash.
            await workflow.RegisterRequestAsync(projectSlug, ToRequestInput(attempt));
            var result = await broker.AuthorizeAsync(attempt);
            switch (result.Disposition)
            {
                case RuntimeBoundaryDisposition.Allowed:
                    run.TryResumeFromApproval(attempt.RequestId);
                    return new("allow", attempt.RequestId,
                        $"Approved by decision {result.DecisionId} for {match.Value.ActionClass}.");
                case RuntimeBoundaryDisposition.Pending when !finalize:
                    return new("pending", attempt.RequestId, "Awaiting an approval decision.");
                case RuntimeBoundaryDisposition.Pending:
                    run.TryResumeFromApproval(attempt.RequestId);
                    return new("deny", attempt.RequestId,
                        "Fail-closed: no approval decision within the enforcement window.");
                case RuntimeBoundaryDisposition.Denied:
                    run.TryResumeFromApproval(attempt.RequestId);
                    return new("deny", attempt.RequestId,
                        $"Denied: no valid, unexpired matching decision for {match.Value.ActionClass}.");
                default:
                    run.TryResumeFromApproval(attempt.RequestId);
                    return new("deny", attempt.RequestId,
                        "Fail-closed: approval lookup or receipt persistence failed.");
            }
        }
        catch (Exception ex)
        {
            if (run is not null && requestId is not null) run.TryResumeFromApproval(requestId);
            return new("deny", requestId, $"Fail-closed: {ex.GetType().Name} while evaluating the boundary.");
        }
    }

    private static RuntimeBoundaryAttempt BuildAttempt(
        string projectSlug, AgentRun run, string toolName, string toolInputJson,
        (BoundaryActionClass ActionClass, string ResourceKind, string ResourceDisplay) match)
    {
        var argumentsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(toolInputJson)));
        // Deterministic id: identical attempts (same run, tool, arguments, class) map to one
        // request, so hook polls stay idempotent and allow-once stays exactly-once across
        // concurrent or restarted attempts.
        var requestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{projectSlug}\n{run.RunId}\n{toolName}\n{argumentsHash}\n{match.ActionClass}\n{AdapterVersion}")))
            .ToLowerInvariant();
        var now = DateTime.UtcNow;
        return new(requestId, requestId, projectSlug, run.RunId, run.TicketId ?? 0, run.AgentName,
            run.CliVersion?.Provider ?? "claude", run.CliVersion?.Version ?? "unknown", match.ActionClass,
            toolName, match.ResourceKind, match.ResourceDisplay, match.ResourceDisplay,
            $"Provider attempted a protected {match.ActionClass} effect ({match.ResourceDisplay}).",
            argumentsHash, now, now.Add(PendingWindow));
    }

    private static ApprovalRequestInput ToRequestInput(RuntimeBoundaryAttempt attempt) => new(
        attempt.RequestId, attempt.DedupeKey, 1, attempt.ActionClass.ToString(), attempt.Operation,
        attempt.ResourceKind, attempt.ResourceCanonicalId, attempt.ResourceDisplay, attempt.Reason, null,
        "temporary", null, attempt.Provider, attempt.ProviderVersion, AdapterVersion, "fail-closed",
        attempt.RunId, attempt.TicketId, attempt.AgentSlug, attempt.AttemptedAt, attempt.ExpiresAt,
        attempt.Operation, attempt.ArgumentsHash, "pre-effect-hook");
}
