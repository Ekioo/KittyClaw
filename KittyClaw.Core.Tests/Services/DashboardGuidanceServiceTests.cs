using System.Text.Json;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class DashboardGuidanceServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kittyclaw-guidance-{Guid.NewGuid():N}");
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Empty_state_is_useful_before_any_first_run_activity()
    {
        var state = await Service().LoadAsync("demo");

        Assert.Equal(DashboardGuidanceStage.Empty, state.Stage);
        Assert.False(state.HasFirstResult);
    }

    [Theory]
    [InlineData("first_run_started", DashboardGuidanceStage.Running)]
    [InlineData("provider_failed", DashboardGuidanceStage.RecoverableError)]
    [InlineData("first_run_completed", DashboardGuidanceStage.Completed)]
    public async Task Restores_progressive_state_after_navigation_or_reload(
        string eventName, DashboardGuidanceStage expected)
    {
        await SeedJourneyAsync("demo", "journey-1", eventName);

        var reloadedService = Service();
        var state = await reloadedService.LoadAsync("demo");

        Assert.Equal(expected, state.Stage);
        Assert.Equal("journey-1", state.JourneyId);
    }

    [Fact]
    public async Task Latest_project_journey_wins_and_malformed_history_is_ignored()
    {
        await SeedJourneyAsync("other", "other-journey", "first_run_completed");
        await SeedJourneyAsync("demo", "demo-journey", "provider_failed");
        await File.AppendAllTextAsync(Path.Combine(_dir, "activation", "first-run-events.jsonl"), "not-json\n");

        var state = await Service().LoadAsync("demo");

        Assert.Equal(DashboardGuidanceStage.RecoverableError, state.Stage);
        Assert.Equal("demo-journey", state.JourneyId);
    }

    [Fact]
    public async Task Fallback_started_after_provider_failure_restores_running_guidance()
    {
        await SeedJourneyAsync("demo", "journey-1", "provider_failed");
        var activation = Path.Combine(_dir, "activation");
        var fallback = new FirstRunActivationEvent(
            "journey-1", "provider_fallback_started", DateTimeOffset.UtcNow.AddSeconds(1));
        await File.AppendAllTextAsync(Path.Combine(activation, "first-run-events.jsonl"),
            JsonSerializer.Serialize(fallback, _json) + "\n");

        var state = await Service().LoadAsync("demo");

        Assert.Equal(DashboardGuidanceStage.Running, state.Stage);
    }

    [Fact]
    public async Task Records_each_required_activation_signal()
    {
        var service = Service();
        var names = new[] { "dashboard_guidance_viewed", "dashboard_primary_action_used",
            "settings_opened_before_first_result", "guidance_replaced_by_activity" };
        foreach (var name in names) await service.RecordAsync("demo", "journey-1", name);

        var text = await File.ReadAllTextAsync(Path.Combine(_dir, "activation", "dashboard-guidance-events.jsonl"));
        foreach (var name in names) Assert.Contains(name, text);
    }

    private DashboardGuidanceService Service() => new(_dir);

    private async Task SeedJourneyAsync(string slug, string journeyId, string runEvent)
    {
        var activation = Path.Combine(_dir, "activation");
        Directory.CreateDirectory(activation);
        var ticket = new FirstTicketEvent(journeyId, "first_ticket_confirmed", DateTimeOffset.UtcNow, 1, slug);
        await File.AppendAllTextAsync(Path.Combine(activation, "first-ticket-events.jsonl"),
            JsonSerializer.Serialize(ticket, _json) + "\n");
        var run = new FirstRunActivationEvent(journeyId, runEvent, DateTimeOffset.UtcNow,
            Error: runEvent == "provider_failed" ? "temporary failure" : null);
        await File.AppendAllTextAsync(Path.Combine(activation, "first-run-events.jsonl"),
            JsonSerializer.Serialize(run, _json) + "\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
