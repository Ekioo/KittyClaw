using System.Text.Json;

namespace KittyClaw.Core.Services;

public enum DashboardGuidanceStage { Empty, Running, RecoverableError, Completed }

public sealed record DashboardGuidanceState(
    string? JourneyId, DashboardGuidanceStage Stage, string? Error = null)
{
    public bool HasFirstResult => Stage == DashboardGuidanceStage.Completed;
}

public sealed class DashboardGuidanceService
{
    private readonly string _activationDirectory;
    private readonly SemaphoreSlim _eventLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DashboardGuidanceService(string dataDirectory) =>
        _activationDirectory = Path.Combine(dataDirectory, "activation");

    public async Task<DashboardGuidanceState> LoadAsync(string projectSlug, CancellationToken ct = default)
    {
        var journeyId = await FindJourneyAsync(projectSlug, ct);
        if (journeyId is null) return new(null, DashboardGuidanceStage.Empty);

        var events = await ReadLinesAsync<FirstRunActivationEvent>("first-run-events.jsonl", ct);
        var journeyEvents = events.Where(e => e.JourneyId == journeyId).OrderBy(e => e.OccurredAt).ToList();
        if (journeyEvents.Any(e => e.Name == "first_run_completed"))
            return new(journeyId, DashboardGuidanceStage.Completed);

        var latestProgress = journeyEvents.LastOrDefault(e => e.Name is
            "first_run_started" or "provider_selected" or "provider_failed" or "provider_fallback_started");
        if (latestProgress?.Name == "provider_failed")
            return new(journeyId, DashboardGuidanceStage.RecoverableError, latestProgress.Error);

        return latestProgress is not null
            ? new(journeyId, DashboardGuidanceStage.Running)
            : new(journeyId, DashboardGuidanceStage.Empty);
    }

    public Task RecordAsync(string projectSlug, string? journeyId, string name, CancellationToken ct = default) =>
        AppendAsync(new DashboardGuidanceEvent(projectSlug, journeyId, name, DateTimeOffset.UtcNow), ct);

    private async Task<string?> FindJourneyAsync(string projectSlug, CancellationToken ct)
    {
        var tickets = await ReadLinesAsync<FirstTicketEvent>("first-ticket-events.jsonl", ct);
        return tickets
            .Where(e => e.Name == "first_ticket_confirmed"
                && (string.IsNullOrWhiteSpace(e.ProjectSlug)
                    || e.ProjectSlug.Equals(projectSlug, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.JourneyId)
            .FirstOrDefault();
    }

    private async Task<List<T>> ReadLinesAsync<T>(string fileName, CancellationToken ct)
    {
        var path = Path.Combine(_activationDirectory, fileName);
        if (!File.Exists(path)) return [];
        var result = new List<T>();
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var value = JsonSerializer.Deserialize<T>(line, JsonOptions);
                if (value is not null) result.Add(value);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private async Task AppendAsync(DashboardGuidanceEvent value, CancellationToken ct)
    {
        Directory.CreateDirectory(_activationDirectory);
        await _eventLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(Path.Combine(_activationDirectory, "dashboard-guidance-events.jsonl"),
                JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
        }
        finally { _eventLock.Release(); }
    }
}

public sealed record DashboardGuidanceEvent(
    string ProjectSlug, string? JourneyId, string Name, DateTimeOffset OccurredAt);
