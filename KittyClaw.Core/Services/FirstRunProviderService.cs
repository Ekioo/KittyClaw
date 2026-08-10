using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Services;

public sealed class FirstRunProviderService
{
    private readonly AgentCliReadinessService _readiness;
    private readonly string _eventFile;
    private readonly SemaphoreSlim _eventLock = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _started = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FirstRunProviderService(string dataDirectory, AgentCliReadinessService readiness)
    {
        _readiness = readiness;
        _eventFile = Path.Combine(dataDirectory, "activation", "first-run-events.jsonl");
    }

    public async Task<FirstRunProviderPlan> SelectAsync(string journeyId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        var started = Stopwatch.GetTimestamp();
        var state = await _readiness.ProbeAsync();
        var providers = new List<FirstRunProvider>();
        if (state.Claude) providers.Add(new("claude", null));
        if (state.Codex) providers.Add(new("codex", "codex:gpt-5.6-sol"));
        if (state.Grok) providers.Add(new("grok", "grok-4.5"));
        if (providers.Count == 0)
            return new(journeyId, null, null,
                "Install Claude Code, OpenAI Codex, or Grok Build, then retry this same step. " +
                "You can configure custom binaries with KITTYCLAW_CLAUDE_BIN, KITTYCLAW_CODEX_BIN, or KITTYCLAW_GROK_BIN.");

        var primary = providers[0];
        var fallback = providers.Skip(1).FirstOrDefault();
        await AppendAsync(new(journeyId, "provider_selected", DateTimeOffset.UtcNow,
            primary.Name, fallback?.Name, Stopwatch.GetElapsedTime(started).TotalMilliseconds), ct);
        return new(journeyId, primary, fallback, null);
    }

    public async Task MarkStartedAsync(string journeyId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        _started.TryAdd(journeyId, Stopwatch.GetTimestamp());
        await AppendAsync(new(journeyId, "first_run_started", DateTimeOffset.UtcNow), ct);
    }

    public async Task RecordFailureAsync(FirstRunProviderPlan plan, string error, CancellationToken ct = default)
    {
        await AppendAsync(new(plan.JourneyId, "provider_failed", DateTimeOffset.UtcNow,
            plan.Primary?.Name, plan.Fallback?.Name, null, error), ct);
        if (plan.Fallback is not null)
            await AppendAsync(new(plan.JourneyId, "provider_fallback_started", DateTimeOffset.UtcNow,
                plan.Fallback.Name), ct);
    }

    public Task RecordCompletedAsync(string journeyId, CancellationToken ct = default) =>
        AppendAsync(new(journeyId, "first_run_completed", DateTimeOffset.UtcNow,
            DurationMilliseconds: _started.TryRemove(journeyId, out var started)
                ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : null), ct);

    private async Task AppendAsync(FirstRunActivationEvent value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_eventFile)!);
        await _eventLock.WaitAsync(ct);
        try { await File.AppendAllTextAsync(_eventFile, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct); }
        finally { _eventLock.Release(); }
    }
}

public sealed record FirstRunProvider(string Name, string? Model);
public sealed record FirstRunProviderPlan(string JourneyId, FirstRunProvider? Primary,
    FirstRunProvider? Fallback, string? Guidance)
{
    public bool Ready => Primary is not null;
}
public sealed record FirstRunActivationEvent(string JourneyId, string Name, DateTimeOffset OccurredAt,
    string? Provider = null, string? FallbackProvider = null, double? DurationMilliseconds = null,
    string? Error = null);
