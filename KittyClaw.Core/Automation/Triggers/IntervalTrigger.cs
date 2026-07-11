using NCrontab;

namespace KittyClaw.Core.Automation.Triggers;

/// <summary>
/// Cron-scheduled trigger. The next fire time is computed once (at registration) and persisted via
/// <see cref="ITriggerStateStore"/> instead of being recomputed from "now" on every tick — so a
/// restart that happens to straddle the scheduled moment still fires on time (or immediately on
/// catch-up if it was missed entirely) rather than silently skipping to the following occurrence.
/// </summary>
public sealed class IntervalTrigger : ITrigger
{
    private readonly CrontabSchedule _schedule;
    private DateTime _nextRunAt;
    private readonly ITriggerStateStore _stateStore;
    private readonly string _slug;
    private readonly string _automationId;

    public IntervalTrigger(IntervalTriggerSpec spec, DateTime nextRunAt, ITriggerStateStore stateStore, string slug, string automationId)
    {
        _schedule = ResolveSchedule(spec);
        _nextRunAt = nextRunAt;
        _stateStore = stateStore;
        _slug = slug;
        _automationId = automationId;
    }

    /// <summary>
    /// Computes the first NextRunAt for a spec that has never been persisted. Callers must persist
    /// the result immediately (before the first tick) so a restart before the scheduled moment
    /// doesn't lose it — see <c>ProjectRuntimeManager.BuildTriggersAsync</c>.
    /// </summary>
    public static DateTime ComputeInitialNextRunAt(IntervalTriggerSpec spec, DateTime now) =>
        ResolveSchedule(spec).GetNextOccurrence(now);

    public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        IReadOnlyList<TriggerFiring> empty = Array.Empty<TriggerFiring>();
        if (_nextRunAt > ctx.Now) return Task.FromResult(empty);

        // Due now, or overdue because the engine wasn't running at the scheduled moment: fire once
        // and re-anchor to the next occurrence from *now* — a multi-day outage catches up with a
        // single fire instead of bursting once per missed occurrence.
        _nextRunAt = _schedule.GetNextOccurrence(ctx.Now);
        IReadOnlyList<TriggerFiring> one = new[] { new TriggerFiring(null, null, null) };
        return Task.FromResult(one);
    }

    public async Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null)
    {
        await _stateStore.SetNextRunAtAsync(_slug, _automationId, _nextRunAt);
    }

    public DateTime? GetNextRunAt(DateTime now) => _nextRunAt;

    private static CrontabSchedule ResolveSchedule(IntervalTriggerSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Cron))
            return CrontabSchedule.Parse(spec.Cron);
        if (spec.Seconds is int seconds && seconds > 0)
            return CrontabSchedule.Parse(SecondsToCron(seconds));
        throw new ArgumentException("IntervalTriggerSpec requires Cron.");
    }

    /// <summary>Best-effort migration for the legacy fixed-interval Seconds field (pre-dating the
    /// cron-only model). Cron's finest grain is 1 minute, so sub-minute intervals collapse to "every
    /// minute" and non-round values round down to the nearest supported step.</summary>
    private static string SecondsToCron(int seconds)
    {
        if (seconds < 120) return "* * * * *";
        var minutes = seconds / 60;
        if (minutes < 60) return $"*/{minutes} * * * *";
        var hours = minutes / 60;
        if (hours < 24) return $"0 */{hours} * * *";
        return "0 0 * * *";
    }
}
