namespace KittyClaw.Core.Automation.Triggers;

/// <summary>Signal emitted by TicketService when a ticket's status changes.</summary>
public sealed record StatusChangeSignal(int TicketId, string From, string To);

/// <summary>
/// Fires when a ticket's status changes, optionally filtered by from/to columns.
/// Uses a persisted snapshot (dispatch-state.json:_ticketSnapshot) to detect changes
/// across restarts.
///
/// A ticket's snapshot is only advanced after the engine confirms dispatch via
/// <see cref="CommitFiring"/>. Firings skipped by transient gates (concurrency,
/// dedup, budget) leave the snapshot at its old value, so the next poll re-fires.
/// Concurrent re-fires during an in-flight run are harmless — the engine's dedup
/// gate absorbs them.
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
        var newSnapshot = new Dictionary<int, string>(current.Count);
        foreach (var (id, status) in current)
        {
            previous.TryGetValue(id, out var prevStatus);
            var shouldFire = prevStatus != status
                && (_spec.From is null || prevStatus == _spec.From)
                && (_spec.To is null || status == _spec.To);

            if (shouldFire)
            {
                var ticket = tickets.First(t => t.Id == id);
                firings.Add(new TriggerFiring(id, ticket.Title, status));
                // Keep old snapshot value so the firing is retried if not committed.
                if (prevStatus is not null) newSnapshot[id] = prevStatus;
            }
            else
            {
                newSnapshot[id] = status;
            }
        }

        ctx.Sessions.SaveTicketSnapshot(ctx.WorkspacePath, newSnapshot);
        return firings;
    }

    public bool TryHandleExternalSignal(object signal, out IReadOnlyList<TriggerFiring> firings)
    {
        if (signal is not StatusChangeSignal s)
        {
            firings = Array.Empty<TriggerFiring>();
            return false;
        }

        var matches = (_spec.From is null || s.From == _spec.From)
                   && (_spec.To   is null || s.To   == _spec.To);

        if (!matches)
        {
            firings = Array.Empty<TriggerFiring>();
            return false;
        }

        // Keep snapshot at old value so the poll retries if commit is skipped.
        firings = [new TriggerFiring(s.TicketId, null, s.To)];
        return true;
    }

    public Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null)
    {
        if (firing.TicketId is int tid && firing.TicketStatus is { } status)
        {
            var snapshot = ctx.Sessions.TicketSnapshot(ctx.WorkspacePath);
            snapshot[tid] = status;
            ctx.Sessions.SaveTicketSnapshot(ctx.WorkspacePath, snapshot);
        }
        return Task.CompletedTask;
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);
}
