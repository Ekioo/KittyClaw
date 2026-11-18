using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class FirstRunProviderServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kittyclaw-first-run-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, false, false, false, "claude", null)]
    [InlineData(false, true, false, false, "codex", "codex:gpt-5.6-sol")]
    [InlineData(false, false, true, false, "grok", "grok-4.5")]
    [InlineData(false, false, false, true, "copilot", "copilot:default")]
    public async Task SelectAsync_UsesEachAvailableProviderIndependently(
        bool claude, bool codex, bool grok, bool copilot, string expectedName, string? expectedModel)
    {
        var plan = await Create(claude, codex, grok, copilot).SelectAsync("journey-1");

        Assert.True(plan.Ready);
        Assert.Equal(expectedName, plan.Primary!.Name);
        Assert.Equal(expectedModel, plan.Primary.Model);
        Assert.Null(plan.Fallback);
    }

    [Fact]
    public async Task SelectAsync_UsesDeterministicFallbackOrder()
    {
        var plan = await Create(true, true, true, false).SelectAsync("journey-2");

        Assert.Equal("claude", plan.Primary!.Name);
        Assert.Equal("codex", plan.Fallback!.Name);
    }

    [Fact]
    public async Task SelectAsync_NoProviderKeepsJourneyForRetry()
    {
        var unavailable = await Create(false, false, false, false).SelectAsync("journey-retry");
        var available = await Create(false, true, false, false).SelectAsync(unavailable.JourneyId);

        Assert.False(unavailable.Ready);
        Assert.Contains("KITTYCLAW_CODEX_BIN", unavailable.Guidance);
        Assert.Contains("KITTYCLAW_COPILOT_BIN", unavailable.Guidance);
        Assert.True(available.Ready);
        Assert.Equal("journey-retry", available.JourneyId);
    }

    [Fact]
    public async Task FailureAndFallback_AreCorrelatedInActivationEvents()
    {
        var service = Create(true, true, false, false);
        var plan = await service.SelectAsync("journey-events");
        await service.MarkStartedAsync(plan.JourneyId);
        await service.RecordFailureAsync(plan, "quota");
        await service.RecordCompletedAsync(plan.JourneyId);

        var events = await File.ReadAllTextAsync(Path.Combine(_dir, "activation", "first-run-events.jsonl"));
        Assert.Contains("provider_failed", events);
        Assert.Contains("provider_fallback_started", events);
        Assert.Contains("first_run_completed", events);
        Assert.DoesNotContain("journey-events\"\n", events);
        Assert.Equal(5, events.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private FirstRunProviderService Create(bool claude, bool codex, bool grok, bool copilot)
    {
        Directory.CreateDirectory(_dir);
        var readiness = new AgentCliReadinessService(
            () => claude ? "claude" : "missing-claude",
            () => codex ? "codex" : null,
            () => grok ? "grok" : null,
            () => null,
            () => copilot ? "copilot" : null,
            (binary, _, _) => Task.FromResult(binary == "git"
                || binary is "claude" or "codex" or "grok" or "copilot"));
        return new FirstRunProviderService(_dir, readiness);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
