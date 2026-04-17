namespace Todo.Core.Automation.Triggers;

/// <summary>
/// Fires when no agent of the project has been dispatched for the configured
/// number of minutes. Reproduces Lain's CEO wake condition B.
/// </summary>
public sealed class AgentInactivityTrigger : ITrigger
{
    private DateTime _lastPolled = DateTime.MinValue;
    private DateTime _lastFired = DateTime.MinValue;
    private readonly AgentInactivityTriggerSpec _spec;

    public AgentInactivityTrigger(AgentInactivityTriggerSpec spec) { _spec = spec; }

    public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());
        _lastPolled = ctx.Now;

        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        DateTime? latest = null;
        foreach (var (key, value) in state)
        {
            if (value is System.Text.Json.Nodes.JsonObject obj
                && obj["lastDispatched"]?.GetValue<string>() is string iso
                && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            {
                if (latest is null || at > latest) latest = at;
            }
        }

        var inactivitySeconds = _spec.MinutesIdle * 60;
        var idleFor = latest is null
            ? double.PositiveInfinity
            : (ctx.Now - latest.Value).TotalSeconds;
        if (idleFor < inactivitySeconds) return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());

        // Don't hammer: fire at most once per inactivity window.
        if ((ctx.Now - _lastFired).TotalSeconds < inactivitySeconds)
            return Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());
        _lastFired = ctx.Now;
        IReadOnlyList<TriggerFiring> one = new[] { new TriggerFiring(null, "agent-inactive", null) };
        return Task.FromResult(one);
    }
}
