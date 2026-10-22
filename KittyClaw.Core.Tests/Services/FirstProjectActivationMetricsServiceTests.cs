using System.Text.Json;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class FirstProjectActivationMetricsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kittyclaw-activation-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _origin = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Calculates_median_rates_abandonments_and_deduplicates_replays()
    {
        await SeedJourney("complete-fast", 0, completedAfterMinutes: 10, duplicateEvents: true);
        await SeedJourney("complete-slow-settings", 30, completedAfterMinutes: 20, settingsAfterMinutes: 4);
        await SeedJourney("abandoned-validation", 60, lastStep: "repository_validated");
        await SeedJourney("abandoned-run-settings", 90, lastStep: "first_run_started", settingsAfterMinutes: 3);

        var report = await new FirstProjectActivationMetricsService(_root).GetReportAsync();

        Assert.Equal(4, report.StartedJourneys);
        Assert.Equal(2, report.CompletedJourneys);
        Assert.Equal(0.5, report.CompletionRate);
        Assert.Equal(15, report.MedianRepositoryToCompletedRunMinutes);
        Assert.Equal(0.5, report.SettingsBeforeFirstResultRate);
        Assert.False(report.MeetsMedianUnder15MinutesTarget);
        Assert.False(report.MeetsSettingsUnder20PercentTarget);
        Assert.Equal(1, report.AbandonmentsByStep["repository_validated"]);
        Assert.Equal(1, report.AbandonmentsByStep["first_run_started"]);
        Assert.Single(report.Events, e => e.JourneyId == "complete-fast" && e.Name == "repository_selected");
        Assert.All(report.Events, e => Assert.Equal(FirstProjectActivationMetricsService.SchemaVersion, e.SchemaVersion));
    }

    [Fact]
    public async Task Empty_or_malformed_input_returns_a_safe_empty_report_without_sensitive_fields()
    {
        Directory.CreateDirectory(Path.Combine(_root, "activation"));
        await File.WriteAllTextAsync(Path.Combine(_root, "activation", "repository-intake-events.jsonl"), "not-json\n");

        var json = JsonSerializer.Serialize(await new FirstProjectActivationMetricsService(_root).GetReportAsync());

        Assert.Contains("\"StartedJourneys\":0", json);
        Assert.DoesNotContain("RepositoryPath", json);
        Assert.DoesNotContain("Objective", json);
        Assert.DoesNotContain("Error", json);
    }

    [Fact]
    public async Task Missing_intermediate_steps_cannot_complete_a_journey()
    {
        await AppendSourceEvent("repository_selected", "missing-steps", _origin);
        await AppendSourceEvent("first_run_completed", "missing-steps", _origin.AddMinutes(5));

        var report = await new FirstProjectActivationMetricsService(_root).GetReportAsync();

        Assert.Equal(0, report.CompletedJourneys);
        Assert.Equal(0, report.CompletionRate);
        Assert.Equal(1, report.AbandonmentsByStep["repository_selected"]);
    }

    [Fact]
    public async Task Out_of_order_steps_stop_at_the_last_coherent_funnel_step()
    {
        await AppendSourceEvent("repository_selected", "out-of-order", _origin);
        await AppendSourceEvent("repository_validated", "out-of-order", _origin.AddMinutes(1));
        await AppendSourceEvent("first_ticket_confirmed", "out-of-order", _origin.AddMinutes(4));
        await AppendSourceEvent("minimal_workflow_ready", "out-of-order", _origin.AddMinutes(3));
        await AppendSourceEvent("first_run_started", "out-of-order", _origin.AddMinutes(5));
        await AppendSourceEvent("first_run_completed", "out-of-order", _origin.AddMinutes(6));

        var report = await new FirstProjectActivationMetricsService(_root).GetReportAsync();

        Assert.Equal(0, report.CompletedJourneys);
        Assert.Equal(1, report.AbandonmentsByStep["first_ticket_confirmed"]);
    }

    private async Task SeedJourney(string id, int offsetMinutes, double? completedAfterMinutes = null,
        string? lastStep = null, double? settingsAfterMinutes = null, bool duplicateEvents = false)
    {
        var start = _origin.AddMinutes(offsetMinutes);
        var steps = new[] { "repository_selected", "repository_validated", "first_ticket_confirmed", "minimal_workflow_ready", "first_run_started" };
        var lastIndex = lastStep is null ? steps.Length - 1 : Array.IndexOf(steps, lastStep);
        for (var i = 0; i <= lastIndex; i++) await AppendSourceEvent(steps[i], id, start.AddMinutes(i));
        if (duplicateEvents) await AppendSourceEvent("repository_selected", id, start.AddSeconds(5));
        if (completedAfterMinutes.HasValue) await AppendSourceEvent("first_run_completed", id, start.AddMinutes(completedAfterMinutes.Value));
        if (settingsAfterMinutes.HasValue)
            await Append("first-project-metric-events.v1.jsonl", new ActivationMetricEvent(1, id, "settings_opened", start.AddMinutes(settingsAfterMinutes.Value)));
    }

    private Task AppendSourceEvent(string name, string id, DateTimeOffset at) => name switch
    {
        "repository_selected" or "repository_validated" => Append("repository-intake-events.jsonl", new RepositoryIntakeEvent(id, name, at, "C:/secret/repository")),
        "first_ticket_confirmed" => Append("first-ticket-events.jsonl", new FirstTicketEvent(id, name, at, 1, "secret-project")),
        "minimal_workflow_ready" => Append("minimal-workflow-events.jsonl", new MinimalWorkflowEvent(id, name, at)),
        _ => Append("first-run-events.jsonl", new FirstRunActivationEvent(id, name, at, Error: "secret agent output"))
    };

    private async Task Append<T>(string file, T value)
    {
        var dir = Path.Combine(_root, "activation");
        Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(Path.Combine(dir, file), JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
