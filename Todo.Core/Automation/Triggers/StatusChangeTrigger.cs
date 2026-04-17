namespace Todo.Core.Automation.Triggers;

/// <summary>
/// Fires when a ticket's status changes, optionally filtered by from/to columns.
/// Uses a persisted snapshot (dispatch-state.json:_ticketSnapshot) to detect changes
/// across restarts.
/// </summary>
public sealed class StatusChangeTrigger : ITrigger
{
    private DateTime _lastPolled = DateTime.MinValue;
    private readonly StatusChangeTriggerSpec _spec;

    public StatusChangeTrigger(StatusChangeTriggerSpec spec) { _spec = spec; }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Array.Empty<TriggerFiring>();
        _lastPolled = ctx.Now;

        var previous = ctx.Sessions.TicketSnapshot(ctx.WorkspacePath);
        var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug);
        var current = tickets.ToDictionary(t => t.Id, t => t.Status);

        var firings = new List<TriggerFiring>();
        foreach (var (id, status) in current)
        {
            previous.TryGetValue(id, out var prevStatus);
            if (prevStatus == status) continue;
            if (_spec.From is not null && prevStatus != _spec.From) continue;
            if (_spec.To is not null && status != _spec.To) continue;
            var ticket = tickets.First(t => t.Id == id);
            firings.Add(new TriggerFiring(id, ticket.Title, status));
        }

        ctx.Sessions.SaveTicketSnapshot(ctx.WorkspacePath, current);
        return firings;
    }
}
