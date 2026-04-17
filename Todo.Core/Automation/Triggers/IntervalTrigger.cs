using NCrontab;

namespace Todo.Core.Automation.Triggers;

public sealed class IntervalTrigger : ITrigger
{
    private DateTime _lastFired = DateTime.MinValue;
    private readonly IntervalTriggerSpec _spec;
    private readonly CrontabSchedule? _schedule;

    public IntervalTrigger(IntervalTriggerSpec spec)
    {
        _spec = spec;
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
}
