using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ApprovalWorkflowServiceTests
{
    [Fact]
    public async Task RequestPausesMatchingRun_AndDecisionResumesIt()
    {
        using var temp = new TempDir();
        var runs = new AgentRunRegistry();
        var run = runs.Register(Run("run-1", "project"));
        var workflow = CreateWorkflow(temp, runs);

        await workflow.RegisterRequestAsync("project", Request("request-1", "run-1"));

        Assert.Equal("request-1", run.AwaitingApprovalRequestId);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "approval_required" && e.CorrelationId == "request-1");

        var now = DateTime.UtcNow;
        await workflow.DecideAsync("project", new("decision-1", "request-1", ApprovalDecisionKind.AllowOnce,
            "owner", now, now.AddMinutes(5), "once", "approved", null));

        Assert.Null(run.AwaitingApprovalRequestId);
        Assert.True(run.SteeringQueue.Reader.TryRead(out var message));
        Assert.Contains("AllowOnce", message);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "approval_decision" && e.CorrelationId == "request-1");
    }

    [Fact]
    public async Task PendingRequest_BlocksExecutionUntilMatchingDecision()
    {
        using var temp = new TempDir();
        var runs = new AgentRunRegistry();
        var run = runs.Register(Run("run-1", "project"));
        var workflow = CreateWorkflow(temp, runs);

        await workflow.RegisterRequestAsync("project", Request("request-1", "run-1"));
        var protectedEffect = false;
        var execution = Task.Run(async () =>
        {
            await run.WaitForApprovalResolutionAsync(CancellationToken.None);
            protectedEffect = true;
        });

        await Task.Delay(150);
        Assert.False(execution.IsCompleted);
        Assert.False(protectedEffect);

        var now = DateTime.UtcNow;
        await workflow.DecideAsync("project", new("decision-1", "request-1", ApprovalDecisionKind.AllowOnce,
            "owner", now, now.AddMinutes(5), "once", "approved", null));

        await execution.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(protectedEffect);
    }

    [Fact]
    public async Task RequestForStoppedRun_RemainsAuditableWithoutTryingToResume()
    {
        using var temp = new TempDir();
        var runs = new AgentRunRegistry();
        var run = runs.Register(Run("run-1", "project"));
        runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
        var workflow = CreateWorkflow(temp, runs);

        var request = await workflow.RegisterRequestAsync("project", Request("request-1", "run-1"));

        Assert.Equal("pending", request.State);
        Assert.Null(run.AwaitingApprovalRequestId);
        Assert.False(run.SteeringQueue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DuplicateRequest_DoesNotPublishAnotherAwaitingSignal()
    {
        using var temp = new TempDir();
        var runs = new AgentRunRegistry();
        var run = runs.Register(Run("run-1", "project"));
        var workflow = CreateWorkflow(temp, runs);
        var request = Request("request-1", "run-1");

        await workflow.RegisterRequestAsync("project", request);
        await workflow.RegisterRequestAsync("project", request with { RequestId = "duplicate-id" });

        Assert.Equal("request-1", run.AwaitingApprovalRequestId);
        Assert.Single(run.SnapshotBuffer(), e => e.Kind == "approval_required");
    }

    private static ApprovalWorkflowService CreateWorkflow(TempDir temp, AgentRunRegistry runs)
    {
        var projects = new ProjectService(temp.Path);
        return new(new ApprovalRegistryService(projects), runs);
    }

    private static AgentRun Run(string id, string project) => new()
    {
        RunId = id,
        ProjectSlug = project,
        TicketId = 169,
        AgentName = "programmer",
        SkillFile = "programmer",
        ConcurrencyGroup = "programmer",
        StartedAt = DateTime.UtcNow
    };

    private static ApprovalRequestInput Request(string id, string runId)
    {
        var now = DateTime.UtcNow;
        return new(id, $"dedupe-{id}", 1, "external_data_transfer", "publish", "network_destination",
            "https://example.test", "example.test", "Publish release artifact", "External transfer", "once", 300,
            "codex", "1.0", "adapter-1", "observe", runId, 169, "programmer", now, now.AddMinutes(10),
            "shell", "SHA256", "tool-call");
    }
}
