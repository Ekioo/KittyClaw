using System.Text.Json;

namespace KittyClaw.Core.Services;

public sealed class FirstProjectActivationMetricsService
{
    public const int SchemaVersion = 1;
    private readonly string _activationDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Funnel =
    [
        "repository_selected", "repository_validated", "first_ticket_confirmed",
        "minimal_workflow_ready", "first_run_started", "first_run_completed"
    ];

    public FirstProjectActivationMetricsService(string dataDirectory) =>
        _activationDirectory = Path.Combine(dataDirectory, "activation");

    public async Task RecordSettingsOpenedAsync(string projectSlug, CancellationToken ct = default)
    {
        var journeyId = (await ReadAsync<FirstTicketEvent>("first-ticket-events.jsonl", ct))
            .Where(e => e.Name == "first_ticket_confirmed" &&
                string.Equals(e.ProjectSlug, projectSlug, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.JourneyId)
            .FirstOrDefault();
        if (journeyId is null) return;

        Directory.CreateDirectory(_activationDirectory);
        await _writeLock.WaitAsync(ct);
        try
        {
            var value = new ActivationMetricEvent(SchemaVersion, journeyId, "settings_opened", DateTimeOffset.UtcNow);
            await File.AppendAllTextAsync(Path.Combine(_activationDirectory, "first-project-metric-events.v1.jsonl"),
                JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<FirstProjectActivationReport> GetReportAsync(CancellationToken ct = default)
    {
        var all = new List<ActivationMetricEvent>();
        all.AddRange((await ReadAsync<RepositoryIntakeEvent>("repository-intake-events.jsonl", ct))
            .Select(e => new ActivationMetricEvent(SchemaVersion, e.JourneyId, e.Name, e.OccurredAt)));
        all.AddRange((await ReadAsync<FirstTicketEvent>("first-ticket-events.jsonl", ct))
            .Select(e => new ActivationMetricEvent(SchemaVersion, e.JourneyId, e.Name, e.OccurredAt)));
        all.AddRange((await ReadAsync<MinimalWorkflowEvent>("minimal-workflow-events.jsonl", ct))
            .Select(e => new ActivationMetricEvent(SchemaVersion, e.JourneyId, e.Name, e.OccurredAt)));
        all.AddRange((await ReadAsync<FirstRunActivationEvent>("first-run-events.jsonl", ct))
            .Select(e => new ActivationMetricEvent(SchemaVersion, e.JourneyId, e.Name, e.OccurredAt,
                e.Provider, e.FallbackProvider, e.DurationMilliseconds, e.Error)));
        all.AddRange((await ReadAsync<DashboardGuidanceEvent>("dashboard-guidance-events.jsonl", ct))
            .Where(e => e.JourneyId is not null && e.Name == "settings_opened_before_first_result")
            .Select(e => new ActivationMetricEvent(SchemaVersion, e.JourneyId!, "settings_opened", e.OccurredAt)));
        all.AddRange(await ReadAsync<ActivationMetricEvent>("first-project-metric-events.v1.jsonl", ct));

        var deduplicated = all
            .Where(e => e.SchemaVersion == SchemaVersion && !string.IsNullOrWhiteSpace(e.JourneyId))
            .GroupBy(e => (e.JourneyId, e.Name))
            .Select(g => g.OrderBy(e => e.OccurredAt).First())
            .OrderBy(e => e.OccurredAt)
            .ToList();
        var journeys = deduplicated.GroupBy(e => e.JourneyId).ToList();
        var started = journeys.Where(j => j.Any(e => e.Name == Funnel[0])).ToList();
        var analyzed = started.Select(j => AnalyzeJourney(j)).ToList();
        var completedDurations = analyzed
            .Where(j => j.CompletedAt.HasValue)
            .Select(j => (j.CompletedAt!.Value - j.StartedAt).TotalMinutes)
            .Order()
            .ToList();

        var abandonments = Funnel.ToDictionary(step => step, _ => 0, StringComparer.Ordinal);
        foreach (var journey in analyzed.Where(j => !j.CompletedAt.HasValue))
            abandonments[Funnel[journey.LastValidStepIndex]]++;

        var settingsBefore = analyzed.Count(j =>
        {
            var settings = j.Events.Where(e => e.Name == "settings_opened").MinBy(e => e.OccurredAt);
            return settings is not null && (!j.CompletedAt.HasValue || settings.OccurredAt < j.CompletedAt.Value);
        });
        var completedCount = completedDurations.Count;
        return new(SchemaVersion, started.Count, completedCount,
            Rate(completedCount, started.Count), Median(completedDurations),
            Rate(settingsBefore, started.Count),
            completedDurations.Count == 0 ? null : Median(completedDurations) < 15,
            started.Count == 0 ? null : Rate(settingsBefore, started.Count) < 0.20,
            abandonments, deduplicated);
    }

    private static JourneyAnalysis AnalyzeJourney(IEnumerable<ActivationMetricEvent> events)
    {
        var byName = events.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var startedAt = byName[Funnel[0]].OccurredAt;
        var previousAt = startedAt;
        var lastValidStepIndex = 0;

        for (var i = 1; i < Funnel.Length; i++)
        {
            if (!byName.TryGetValue(Funnel[i], out var current) || current.OccurredAt < previousAt)
                break;

            previousAt = current.OccurredAt;
            lastValidStepIndex = i;
        }

        return new(events, startedAt, lastValidStepIndex,
            lastValidStepIndex == Funnel.Length - 1 ? previousAt : null);
    }

    private async Task<List<T>> ReadAsync<T>(string fileName, CancellationToken ct)
    {
        var path = Path.Combine(_activationDirectory, fileName);
        if (!File.Exists(path)) return [];
        var result = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<T>(line, JsonOptions) is { } value) result.Add(value); }
            catch (JsonException) { }
        }
        return result;
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round((double)numerator / denominator, 4);

    private static double? Median(List<double> values) => values.Count switch
    {
        0 => null,
        _ when values.Count % 2 == 1 => values[values.Count / 2],
        _ => (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2
    };

    private sealed record JourneyAnalysis(
        IEnumerable<ActivationMetricEvent> Events,
        DateTimeOffset StartedAt,
        int LastValidStepIndex,
        DateTimeOffset? CompletedAt);
}

public sealed record ActivationMetricEvent(int SchemaVersion, string JourneyId, string Name, DateTimeOffset OccurredAt,
    string? Provider = null, string? FallbackProvider = null, double? DurationMilliseconds = null,
    string? Error = null);

public sealed record FirstProjectActivationReport(
    int SchemaVersion,
    int StartedJourneys,
    int CompletedJourneys,
    double CompletionRate,
    double? MedianRepositoryToCompletedRunMinutes,
    double SettingsBeforeFirstResultRate,
    bool? MeetsMedianUnder15MinutesTarget,
    bool? MeetsSettingsUnder20PercentTarget,
    IReadOnlyDictionary<string, int> AbandonmentsByStep,
    IReadOnlyList<ActivationMetricEvent> Events);
