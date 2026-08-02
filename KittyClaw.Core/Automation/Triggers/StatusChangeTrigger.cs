namespace KittyClaw.Core.Automation.Triggers;

/// <summary>Signal emitted by TicketService when a ticket's status changes.</summary>
public sealed record StatusChangeSignal(int TicketId, string From, string To);

/// <summary>
/// Fires when a ticket's status changes, optionally filtered by from/to columns.
/// Uses a persisted snapshot (dispatch-state.json:_ticketSnapshot) to detect changes
/// across restarts.
///
/// A matching transition is atomically consumed per automation immediately before
/// dispatch. This makes delivery durable across polling, reloads, restarts, and
/// concurrent duplicate observations while preserving independent automations.
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

        // Snapshots are per automation (§2.4): another workflow committing its own firing
        // must never acknowledge a transition THIS automation still has to retry.
        var previous = ctx.Sessions.TicketSnapshot(ctx.WorkspacePath, ctx.Automation.Id);
        var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug);
        var current = tickets.ToDictionary(t => t.Id, t => t.Status);

        var firings = new List<TriggerFiring>();
        var newSnapshot = new Dictionary<int, string>(current.Count);
        foreach (var (id, status) in current)
        {
            previous.TryGetValue(id, out var prevStatus);
            // A missing entry is baseline discovery, not evidence of a transition. This
            // happens for tickets created while the engine is down and, importantly, when
            // migrating a partial legacy snapshot. Treating null -> Done as a status change
            // replays every historical Done ticket after a restart.
            var shouldFire = prevStatus is not null
                && prevStatus != status
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

        ctx.Sessions.SaveTicketSnapshot(ctx.WorkspacePath, ctx.Automation.Id, newSnapshot);
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
            // The transition still advances this automation's durable baseline. Without
            // this observation, Done -> Review -> Done signals received between polls leave
            // the baseline at Done and suppress the legitimate second entry.
            firings = [new TriggerFiring(s.TicketId, null, s.To, ShouldDispatch: false)];
            return true;
        }

        // Keep snapshot at old value so the poll retries if commit is skipped.
        firings = [new TriggerFiring(s.TicketId, null, s.To)];
        return true;
    }

    public Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null)
    {
        TryConsumeFiring(ctx, firing);
        return Task.CompletedTask;
    }

    public bool TryConsumeFiring(TriggerContext ctx, TriggerFiring firing) =>
        firing.TicketId is int tid && firing.TicketStatus is { } status
            && ctx.Sessions.TryConsumeStatusTransition(ctx.WorkspacePath, ctx.Automation.Id, tid, status);

    public Task ConsumeSignalFiringAsync(TriggerContext ctx, TriggerFiring firing)
    {
        TryConsumeFiring(ctx, firing);
        return Task.CompletedTask;
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);
}
