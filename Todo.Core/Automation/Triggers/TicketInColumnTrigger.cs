namespace Todo.Core.Automation.Triggers;

/// <summary>
/// Fires once per matching ticket (column + optional assignee filter).
/// The poll interval gates evaluation; between polls, no evaluation happens.
/// Dedup against active runs is handled by the engine (via AgentRunRegistry).
/// </summary>
public sealed class TicketInColumnTrigger : ITrigger
{
    private DateTime _lastEvaluated = DateTime.MinValue;
    private readonly TicketInColumnTriggerSpec _spec;

    public TicketInColumnTrigger(TicketInColumnTriggerSpec spec) { _spec = spec; }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastEvaluated).TotalSeconds < _spec.Seconds)
            return Array.Empty<TriggerFiring>();
        _lastEvaluated = ctx.Now;

        if (_spec.Columns.Count == 0) return Array.Empty<TriggerFiring>();

        var firings = new List<TriggerFiring>();
        foreach (var col in _spec.Columns)
        {
            var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug, statusFilter: col);
            foreach (var t in tickets)
            {
                if (t.AssignedTo is null) continue;
                if (!string.IsNullOrEmpty(_spec.AssigneeSlug) && t.AssignedTo != _spec.AssigneeSlug) continue;
                firings.Add(new TriggerFiring(t.Id, t.Title, t.Status));
            }
        }
        return firings;
    }
}
