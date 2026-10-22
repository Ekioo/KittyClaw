using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class RuntimeBoundaryEnforcementServiceTests
{
    public static TheoryData<CliProvider, BoundaryActionClass> BrokerMatrix()
    {
        var data = new TheoryData<CliProvider, BoundaryActionClass>();
        foreach (var provider in Enum.GetValues<CliProvider>())
        foreach (var boundary in Enum.GetValues<BoundaryActionClass>()) data.Add(provider, boundary);
        return data;
    }

    [Theory]
    [MemberData(nameof(BrokerMatrix))]
    public async Task BrokeredAdapter_AllSupportedRuntimesAndBoundariesFailClosedUntilAllowed(
        CliProvider provider, BoundaryActionClass boundary)
    {
        using var temp = new TempDir();
        var registry = new ApprovalRegistryService(new ProjectService(temp.Path));
        var service = new RuntimeBoundaryEnforcementService(registry);
        var attempt = Attempt(provider, boundary);
        var effects = 0;

        var pending = await service.ExecuteAsync(attempt, _ => { effects++; return Task.CompletedTask; });
        Assert.Equal(RuntimeBoundaryDisposition.Pending, pending.Disposition);
        Assert.Equal(0, effects);

        var now = DateTime.UtcNow;
        await registry.DecideAsync("project", new("decision", attempt.RequestId, ApprovalDecisionKind.AllowOnce,
            "owner", now, now.AddMinutes(5), "once", "approved", null));
        var allowed = await service.ExecuteAsync(attempt, _ => { effects++; return Task.CompletedTask; });

        Assert.Equal(RuntimeBoundaryDisposition.EffectSucceeded, allowed.Disposition);
        Assert.Equal(1, effects);
        Assert.Single(await registry.QueryReceiptsAsync("project", attempt.RequestId));
    }

    [Fact]
    public async Task AllowOnce_IsConsumedExactlyOnceAcrossConcurrentAttempts()
    {
        using var temp = new TempDir();
        var registry = new ApprovalRegistryService(new ProjectService(temp.Path));
        var service = new RuntimeBoundaryEnforcementService(registry);
        var attempt = Attempt(CliProvider.Codex, BoundaryActionClass.PublishOrDeploy);
        await service.ExecuteAsync(attempt, _ => Task.CompletedTask);
        var now = DateTime.UtcNow;
        await registry.DecideAsync("project", new("decision", attempt.RequestId, ApprovalDecisionKind.AllowOnce,
            "owner", now, now.AddMinutes(5), "once", "approved", null));
        var effects = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.ExecuteAsync(attempt, _ => { Interlocked.Increment(ref effects); return Task.CompletedTask; })));

        Assert.Equal(1, effects);
        Assert.Single(results, x => x.Disposition == RuntimeBoundaryDisposition.EffectSucceeded);
    }

    [Fact]
    public async Task AllowForTicket_AppliesToLaterMatchingEffectsOnly()
    {
        using var temp = new TempDir();
        var registry = new ApprovalRegistryService(new ProjectService(temp.Path));
        var service = new RuntimeBoundaryEnforcementService(registry);
        var first = Attempt(CliProvider.Claude, BoundaryActionClass.PushOrPullRequest);
        await service.ExecuteAsync(first, _ => Task.CompletedTask);
        var now = DateTime.UtcNow;
        await registry.DecideAsync("project", new("ticket-grant", first.RequestId,
            ApprovalDecisionKind.AllowForTicket, "owner", now, now.AddHours(1),
            $"ticket:{first.TicketId}", "approved for this ticket", null));
        var matching = first with { RequestId = "matching-request", DedupeKey = "matching-dedupe" };
        var otherResource = first with
        {
            RequestId = "other-resource-request",
            DedupeKey = "other-resource-dedupe",
            ResourceCanonicalId = "other-resource"
        };
        var effects = 0;

        var allowed = await service.ExecuteAsync(matching,
            _ => { effects++; return Task.CompletedTask; });
        var pending = await service.ExecuteAsync(otherResource,
            _ => { effects++; return Task.CompletedTask; });

        Assert.Equal(RuntimeBoundaryDisposition.EffectSucceeded, allowed.Disposition);
        Assert.Equal(RuntimeBoundaryDisposition.Pending, pending.Disposition);
        Assert.Equal(1, effects);
        Assert.Equal("allowed", (await registry.QueryRequestsAsync("project", new()))
            .Single(request => request.RequestId == matching.RequestId).State);
        var receipt = Assert.Single(await registry.QueryReceiptsAsync("project", matching.RequestId),
            item => item.Outcome == ApprovalReceiptOutcome.Allowed);
        Assert.Equal("ticket-grant", receipt.DecisionId);
    }

    [Fact]
    public async Task AuthorizeAsync_FailsClosedWhenReceiptCannotBePersisted()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var registry = new ApprovalRegistryService(projects);
        var service = new RuntimeBoundaryEnforcementService(registry);
        var attempt = Attempt(CliProvider.Claude, BoundaryActionClass.PushOrPullRequest);

        // Make the per-project database unopenable so registration/receipt persistence throws.
        Directory.CreateDirectory(projects.GetProjectDbPath("project"));
        var effects = 0;

        var result = await service.ExecuteAsync(attempt, _ => { effects++; return Task.CompletedTask; });

        Assert.Equal(RuntimeBoundaryDisposition.FailedClosed, result.Disposition);
        Assert.Equal(0, effects);
    }

    [Fact]
    public void Catalogue_OnlyProvidersWithPreEffectHooksAdvertiseProtection()
    {
        foreach (var boundary in Enum.GetValues<BoundaryActionClass>())
        {
            Assert.True(RuntimeEnforcementCapabilities.CanAdvertiseProtection(CliProvider.Claude, boundary));
            foreach (var provider in new[] { CliProvider.Codex, CliProvider.Grok, CliProvider.Mistral })
            {
                Assert.False(RuntimeEnforcementCapabilities.CanAdvertiseProtection(provider, boundary));
                var capability = RuntimeEnforcementCapabilities.Catalogue
                    .Single(x => x.Provider == provider && x.Boundary == boundary);
                Assert.Equal(RuntimeEnforcementLevel.ObservationOnly, capability.Level);
                Assert.False(string.IsNullOrWhiteSpace(capability.Exclusion));
            }
        }

        Assert.Empty(RuntimeEnforcementCapabilities.UnenforceableBoundaries(CliProvider.Claude));
        Assert.Equal(Enum.GetValues<BoundaryActionClass>().Length,
            RuntimeEnforcementCapabilities.UnenforceableBoundaries(CliProvider.Codex).Count);
        // One capability row per provider × boundary pair — the matrix is total and unambiguous.
        Assert.Equal(Enum.GetValues<CliProvider>().Length * Enum.GetValues<BoundaryActionClass>().Length,
            RuntimeEnforcementCapabilities.Catalogue.Count);
    }

    private static RuntimeBoundaryAttempt Attempt(CliProvider provider, BoundaryActionClass boundary)
    {
        var now = DateTime.UtcNow;
        return new($"request-{provider}-{boundary}", $"dedupe-{provider}-{boundary}", "project", "run", 170,
            "programmer", provider.ToString().ToLowerInvariant(), "test", boundary, boundary.ToString(), "resource",
            "resource-id", "redacted resource", "protected test effect", "SHA256:redacted", now, now.AddMinutes(10));
    }
}
