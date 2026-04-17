using System.Text.Json.Nodes;

namespace Todo.Core.Automation.Triggers;

/// <summary>
/// Fires on a parent ticket when the CSV of its sub-ticket statuses changes
/// compared to the previously recorded one. Reproduces Lain's
/// `producer.lastSubStatuses` gating to avoid re-dispatching the producer while
/// nothing actionable has changed in the sub-tree.
/// </summary>
public sealed class SubTicketStatusTrigger : ITrigger
{
    private DateTime _lastPolled = DateTime.MinValue;
    private readonly SubTicketStatusTriggerSpec _spec;

    public SubTicketStatusTrigger(SubTicketStatusTriggerSpec spec) { _spec = spec; }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Array.Empty<TriggerFiring>();
        _lastPolled = ctx.Now;

        var parents = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug,
            statusFilter: _spec.ParentColumn);

        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        var agentKey = ctx.Automation.Id; // per-automation bucket
        var bucket = state[agentKey] as JsonObject ?? new JsonObject();
        var lastSubs = bucket["lastSubStatuses"] as JsonObject ?? new JsonObject();

        var firings = new List<TriggerFiring>();
        foreach (var parent in parents)
        {
            if (parent.SubTickets.Count == 0) continue;
            var csv = string.Join(",", parent.SubTickets
                .OrderBy(s => s.Id)
                .Select(s => $"{s.Id}:{s.Status}"));
            var prev = lastSubs[parent.Id.ToString()]?.GetValue<string>();
            if (prev == csv) continue;

            // Debounce check
            if (_spec.DebounceSeconds is not null)
            {
                var lastAt = ctx.Sessions.LastDispatched(ctx.WorkspacePath, agentKey);
                if (lastAt is not null && (ctx.Now - lastAt.Value).TotalSeconds < _spec.DebounceSeconds.Value)
                    continue;
            }

            lastSubs[parent.Id.ToString()] = csv;
            firings.Add(new TriggerFiring(parent.Id, parent.Title, parent.Status));
        }

        if (firings.Count > 0)
        {
            bucket["lastSubStatuses"] = lastSubs;
            state[agentKey] = bucket;
            ctx.Sessions.Save(ctx.WorkspacePath, state);
        }
        return firings;
    }
}
