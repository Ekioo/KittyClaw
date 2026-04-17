namespace Todo.Core.Automation.Triggers;

/// <summary>
/// Fires when the board is idle (no ticket outside the configured idle columns).
/// Reproduces Lain's CEO wake condition A.
/// </summary>
public sealed class BoardIdleTrigger : ITrigger
{
    private DateTime _lastPolled = DateTime.MinValue;
    private bool _lastWasIdle;
    private readonly BoardIdleTriggerSpec _spec;

    public BoardIdleTrigger(BoardIdleTriggerSpec spec) { _spec = spec; }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Array.Empty<TriggerFiring>();
        _lastPolled = ctx.Now;

        var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug);
        var idleSet = new HashSet<string>(_spec.IdleColumns, StringComparer.OrdinalIgnoreCase);
        var isIdle = tickets.All(t => idleSet.Contains(t.Status));

        // Only fire on edge (transition to idle), not continuously.
        var fired = isIdle && !_lastWasIdle;
        _lastWasIdle = isIdle;
        return fired
            ? new[] { new TriggerFiring(null, "board-idle", null) }
            : Array.Empty<TriggerFiring>();
    }
}
