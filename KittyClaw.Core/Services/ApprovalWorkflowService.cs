using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed class ApprovalWorkflowService(ApprovalRegistryService registry, AgentRunRegistry runs)
{
    public async Task<ApprovalRequestRecord> RegisterRequestAsync(string projectSlug, ApprovalRequestInput input)
    {
        var request = await registry.RegisterRequestAsync(projectSlug, input);
        var run = runs.Get(request.RunId);
        if (run is not null && run.ProjectSlug == projectSlug && run.Status == AgentRunStatus.Running
            && request.State == "pending" && run.TryPauseForApproval(request.RequestId))
        {
            run.Push(new(DateTime.UtcNow, "approval_required", request.Reason,
                $"{request.ActionClass}: {request.ResourceDisplay}", request.RequestId));
        }
        return request;
    }

    public async Task<ApprovalDecisionRecord> DecideAsync(string projectSlug, ApprovalDecisionInput input)
    {
        var decision = await registry.DecideAsync(projectSlug, input);
        var request = (await registry.QueryRequestsAsync(projectSlug, new()))
            .Single(r => r.RequestId == input.RequestId);
        var run = runs.Get(request.RunId);
        if (run is not null && run.ProjectSlug == projectSlug && run.Status == AgentRunStatus.Running
            && run.TryResumeFromApproval(request.RequestId))
        {
            var message = $"[Runtime approval {decision.Kind}: request {request.RequestId}; scope={decision.Scope}; expires={decision.ExpiresAt:O}]";
            await run.SteeringQueue.Writer.WriteAsync(message);
            run.Push(new(DateTime.UtcNow, "approval_decision", decision.Kind.ToString(), decision.Scope, request.RequestId));
        }
        return decision;
    }
}
