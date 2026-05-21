using NCrontab;

namespace KittyClaw.Core.Automation.Triggers;

public sealed class IntervalTrigger : ITrigger
{
    private DateTime _lastFired;
    private readonly IntervalTriggerSpec _spec;
    private readonly CrontabSchedule? _schedule;
    private readonly ITriggerStateStore _stateStore;
    private readonly string _slug;
    private readonly string _automationId;

    public IntervalTrigger(IntervalTriggerSpec spec, DateTime lastFired, ITriggerStateStore stateStore, string slug, string automationId)
    {
        _spec = spec;
        _lastFired = lastFired;
        _stateStore = stateStore;
        _slug = slug;
        _automationId = automationId;
        if (!string.IsNullOrWhiteSpace(spec.Cron))
            _schedule = CrontabSchedule.Parse(spec.Cron);
    }

    public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        IReadOnlyList<TriggerFiring> empty = Array.Empty<TriggerFiring>();
        var now = ctx.Now;
        bool shouldFire;
        if (_schedule is not null)
        {
            var baseline = _lastFired == DateTime.MinValue ? now.AddSeconds(-1) : _lastFired;
            var next = _schedule.GetNextOccurrence(baseline);
            shouldFire = next <= now;
        }
        else
        {
            var seconds = _spec.Seconds ?? 60;
            shouldFire = (now - _lastFired).TotalSeconds >= seconds;
        }
        if (!shouldFire) return Task.FromResult(empty);
        _lastFired = now;
        IReadOnlyList<TriggerFiring> one = new[] { new TriggerFiring(null, null, null) };
        return Task.FromResult(one);
    }

    public async Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null)
    {
        await _stateStore.SetLastRunAtAsync(_slug, _automationId, _lastFired);
    }

    public DateTime? GetNextRunAt(DateTime now)
    {
        if (_schedule is not null)
        {
            var baseline = _lastFired == DateTime.MinValue ? now.AddSeconds(-1) : _lastFired;
            return _schedule.GetNextOccurrence(baseline);
        }
        var seconds = _spec.Seconds ?? 60;
        return _lastFired == DateTime.MinValue ? now : _lastFired.AddSeconds(seconds);
    }
}
