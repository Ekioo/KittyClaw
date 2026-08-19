using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class RuntimeBoundaryGateServiceTests
{
    public static TheoryData<CliProvider, BoundaryActionClass> ProviderBoundaryMatrix()
    {
        var data = new TheoryData<CliProvider, BoundaryActionClass>();
        foreach (var provider in Enum.GetValues<CliProvider>())
        foreach (var boundary in Enum.GetValues<BoundaryActionClass>()) data.Add(provider, boundary);
        return data;
    }

    // Executable adapter matrix (acceptance criterion 1): every declared runtime × boundary pair
    // either proves pre-effect interception through the gate, or is an explicit exclusion whose
    // protected dispatch would fail closed — never a silent unprotected claim.
    [Theory]
    [MemberData(nameof(ProviderBoundaryMatrix))]
    public async Task Matrix_EnforcedPairsGateBeforeEffect_ExcludedPairsAreExplicit(
        CliProvider provider, BoundaryActionClass boundary)
    {
        if (RuntimeEnforcementCapabilities.CanAdvertiseProtection(provider, boundary))
        {
            Assert.Equal(CliProvider.Claude, provider);
            using var harness = new GateHarness();
            var payload = Payload(CommandFor(boundary));

            var pending = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
            Assert.Equal("pending", pending.Decision);
            Assert.Equal(pending.RequestId, harness.Run.AwaitingApprovalRequestId);

            var now = DateTime.UtcNow;
            await harness.Registry.DecideAsync("project", new($"decision-{boundary}", pending.RequestId!,
                ApprovalDecisionKind.AllowOnce, "owner", now, now.AddMinutes(5), "once", "approved", null));
            var allowed = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);

            Assert.Equal("allow", allowed.Decision);
            Assert.Null(harness.Run.AwaitingApprovalRequestId);
            Assert.Contains(await harness.Registry.QueryReceiptsAsync("project", pending.RequestId),
                r => r.Outcome == ApprovalReceiptOutcome.Allowed);
        }
        else
        {
            var capability = RuntimeEnforcementCapabilities.Catalogue
                .Single(x => x.Provider == provider && x.Boundary == boundary);
            Assert.Equal(RuntimeEnforcementLevel.ObservationOnly, capability.Level);
            Assert.False(string.IsNullOrWhiteSpace(capability.Exclusion));
            Assert.Contains(boundary, RuntimeEnforcementCapabilities.UnenforceableBoundaries(provider));
        }
    }

    [Fact]
    public async Task OrdinaryTool_IsAllowedWithoutRegisteringAnyApprovalRequest()
    {
        using var harness = new GateHarness();

        var verdict = await harness.Gate.EvaluateHookAsync("project", "run-1",
            Payload("dotnet build KittyClaw.sln"), finalize: false);

        Assert.Equal("allow", verdict.Decision);
        Assert.Empty(await harness.Registry.QueryRequestsAsync("project", new()));
        Assert.Null(harness.Run.AwaitingApprovalRequestId);
    }

    [Fact]
    public async Task LocalOrKnownDestination_IsNotANewNetworkBoundary()
    {
        using var harness = new GateHarness();

        var local = await harness.Gate.EvaluateHookAsync("project", "run-1",
            Payload("curl -s http://localhost:5230/api/docs"), finalize: false);

        Assert.Equal("allow", local.Decision);
        Assert.Empty(await harness.Registry.QueryRequestsAsync("project", new()));
    }

    [Fact]
    public async Task PendingWithoutDecision_DeniesOnFinalizeAndResumesTheRun()
    {
        using var harness = new GateHarness();
        var payload = Payload("git push origin main");

        var pending = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        Assert.Equal("pending", pending.Decision);
        Assert.NotNull(harness.Run.AwaitingApprovalRequestId);

        var denied = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: true);

        Assert.Equal("deny", denied.Decision);
        Assert.Null(harness.Run.AwaitingApprovalRequestId);
        var request = Assert.Single(await harness.Registry.QueryRequestsAsync("project", new()));
        Assert.Equal("pending", request.State);
    }

    [Fact]
    public async Task DeniedDecision_DeniesAndPersistsTheDeniedReceipt()
    {
        using var harness = new GateHarness();
        var payload = Payload("npm publish");
        var pending = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        var now = DateTime.UtcNow;
        await harness.Registry.DecideAsync("project", new("decision", pending.RequestId!,
            ApprovalDecisionKind.Deny, "owner", now, now.AddMinutes(5), "once", "not allowed", null));

        var verdict = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);

        Assert.Equal("deny", verdict.Decision);
        Assert.Contains(await harness.Registry.QueryReceiptsAsync("project", pending.RequestId),
            r => r.Outcome == ApprovalReceiptOutcome.Denied);
    }

    [Fact]
    public async Task ExpiredDecision_DeniesAndPersistsTheExpiredReceipt()
    {
        using var harness = new GateHarness();
        var payload = Payload("git push origin main");
        var pending = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        var now = DateTime.UtcNow;
        await harness.Registry.DecideAsync("project", new("decision", pending.RequestId!,
            ApprovalDecisionKind.AllowOnce, "owner", now, now.AddMilliseconds(50), "once", "approved", null));
        await Task.Delay(120);

        var verdict = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);

        Assert.Equal("deny", verdict.Decision);
        Assert.Contains(await harness.Registry.QueryReceiptsAsync("project", pending.RequestId),
            r => r.Outcome == ApprovalReceiptOutcome.Expired);
    }

    [Fact]
    public async Task AllowOnce_PermitsExactlyOneMatchingEffectUnderConcurrentAttempts()
    {
        using var harness = new GateHarness();
        var payload = Payload("git push origin main");
        var pending = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        var now = DateTime.UtcNow;
        await harness.Registry.DecideAsync("project", new("decision", pending.RequestId!,
            ApprovalDecisionKind.AllowOnce, "owner", now, now.AddMinutes(5), "once", "approved", null));

        var verdicts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false)));

        Assert.Single(verdicts, v => v.Decision == "allow");
        Assert.Equal(7, verdicts.Count(v => v.Decision == "deny"));
        Assert.Single(await harness.Registry.QueryReceiptsAsync("project", pending.RequestId),
            r => r.Outcome == ApprovalReceiptOutcome.Allowed);
    }

    [Fact]
    public async Task AllowOnceConsumption_SurvivesRestartWithoutDuplicatingTheEffect()
    {
        using var temp = new TempDir();
        string? requestId;
        using (var first = new GateHarness(temp))
        {
            var pending = await first.Gate.EvaluateHookAsync("project", "run-1",
                Payload("git push origin main"), finalize: false);
            requestId = pending.RequestId;
            var now = DateTime.UtcNow;
            await first.Registry.DecideAsync("project", new("decision", requestId!,
                ApprovalDecisionKind.AllowOnce, "owner", now, now.AddMinutes(5), "once", "approved", null));
            Assert.Equal("allow", (await first.Gate.EvaluateHookAsync("project", "run-1",
                Payload("git push origin main"), finalize: false)).Decision);
        }

        // Fresh service instances over the same store simulate a restart between decision and a
        // replayed attempt: the consumed allow-once must never authorize a second effect.
        using var second = new GateHarness(temp);
        var verdict = await second.Gate.EvaluateHookAsync("project", "run-1",
            Payload("git push origin main"), finalize: false);

        Assert.Equal("deny", verdict.Decision);
        Assert.Single(await second.Registry.QueryReceiptsAsync("project", requestId),
            r => r.Outcome == ApprovalReceiptOutcome.Allowed);
    }

    [Fact]
    public async Task AllowForTicket_IsScopedToTheMatchingTicketOnly()
    {
        using var harness = new GateHarness();
        var payload = Payload("git push origin main");
        var pending = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        var now = DateTime.UtcNow;
        await harness.Registry.DecideAsync("project", new("decision", pending.RequestId!,
            ApprovalDecisionKind.AllowForTicket, "owner", now, now.AddMinutes(30), "ticket:999", "approved", null));

        var mismatched = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        Assert.Equal("deny", mismatched.Decision);

        await harness.Registry.DecideAsync("project", new("decision-2", pending.RequestId!,
            ApprovalDecisionKind.AllowForTicket, "owner", now, now.AddMinutes(30), "ticket:170", "approved",
            "decision"));

        var first = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        var second = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        Assert.Equal("allow", first.Decision);
        Assert.Equal("allow", second.Decision);
    }

    [Fact]
    public async Task UnknownRun_IsDeniedFailClosed()
    {
        using var harness = new GateHarness();

        var verdict = await harness.Gate.EvaluateHookAsync("project", "no-such-run",
            Payload("git push origin main"), finalize: false);

        Assert.Equal("deny", verdict.Decision);
        Assert.Contains("Fail-closed", verdict.Reason);
    }

    [Fact]
    public async Task MalformedHookPayload_IsDeniedFailClosed()
    {
        using var harness = new GateHarness();

        var verdict = await harness.Gate.EvaluateHookAsync("project", "run-1", "not json at all", finalize: false);

        Assert.Equal("deny", verdict.Decision);
        Assert.Contains("Fail-closed", verdict.Reason);
    }

    [Fact]
    public async Task UnavailableRegistry_IsDeniedFailClosed()
    {
        using var harness = new GateHarness();
        // Make the per-project database unopenable: registration and receipts cannot persist.
        Directory.CreateDirectory(harness.Projects.GetProjectDbPath("project"));

        var verdict = await harness.Gate.EvaluateHookAsync("project", "run-1",
            Payload("git push origin main"), finalize: false);

        Assert.Equal("deny", verdict.Decision);
        Assert.Contains("Fail-closed", verdict.Reason);
    }

    [Fact]
    public async Task PostToolUse_RecordsTheTerminalEffectOutcomeReceipt()
    {
        using var harness = new GateHarness();
        var payload = Payload("git push origin main");
        var pending = await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false);
        var now = DateTime.UtcNow;
        await harness.Registry.DecideAsync("project", new("decision", pending.RequestId!,
            ApprovalDecisionKind.AllowOnce, "owner", now, now.AddMinutes(5), "once", "approved", null));
        Assert.Equal("allow", (await harness.Gate.EvaluateHookAsync("project", "run-1", payload, finalize: false)).Decision);

        var post = await harness.Gate.EvaluateHookAsync("project", "run-1",
            Payload("git push origin main", eventName: "PostToolUse"), finalize: false);

        Assert.Equal("allow", post.Decision);
        Assert.Contains(await harness.Registry.QueryReceiptsAsync("project", pending.RequestId),
            r => r.Outcome == ApprovalReceiptOutcome.EffectSucceeded);
    }

    private static string CommandFor(BoundaryActionClass boundary) => boundary switch
    {
        BoundaryActionClass.PushOrPullRequest => "git push origin main",
        BoundaryActionClass.PublishOrDeploy => "npm publish",
        BoundaryActionClass.SecretAccess => "cat .env",
        BoundaryActionClass.DestructiveOperation => "rm -rf build",
        _ => "curl https://boundary.example.test/upload",
    };

    private static string Payload(string command, string eventName = "PreToolUse")
    {
        var toolInput = JsonSerializer.Serialize(new { command });
        var response = eventName == "PostToolUse" ? ",\"tool_response\":{\"success\":true}" : "";
        return $"{{\"session_id\":\"s\",\"hook_event_name\":\"{eventName}\",\"tool_name\":\"Bash\"," +
            $"\"tool_input\":{toolInput}{response}}}";
    }

    private sealed class GateHarness : IDisposable
    {
        private readonly TempDir? _ownedTemp;

        public GateHarness(TempDir? temp = null)
        {
            _ownedTemp = temp is null ? new TempDir() : null;
            Projects = new ProjectService((temp ?? _ownedTemp!).Path);
            Registry = new ApprovalRegistryService(Projects);
            var runs = new AgentRunRegistry();
            Run = runs.Register(new AgentRun
            {
                RunId = "run-1",
                ProjectSlug = "project",
                TicketId = 170,
                AgentName = "programmer",
                SkillFile = "programmer",
                ConcurrencyGroup = "programmer",
                StartedAt = DateTime.UtcNow,
            });
            Gate = new RuntimeBoundaryGateService(
                new RuntimeBoundaryEnforcementService(Registry),
                new ApprovalWorkflowService(Registry, runs),
                runs,
                new BoundaryObservationService(Projects, NullLogger<BoundaryObservationService>.Instance));
        }

        public ProjectService Projects { get; }
        public ApprovalRegistryService Registry { get; }
        public AgentRun Run { get; }
        public RuntimeBoundaryGateService Gate { get; }

        public void Dispose() => _ownedTemp?.Dispose();
    }
}
