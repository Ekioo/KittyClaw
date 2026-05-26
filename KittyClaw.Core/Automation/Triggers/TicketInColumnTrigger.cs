using System.Text.Json.Nodes;

namespace KittyClaw.Core.Automation.Triggers;

/// <summary>
/// Fires once per matching ticket (column + optional assignee filter).
/// The poll interval gates evaluation; between polls, no evaluation happens.
/// Dedup against active runs is handled by the engine (via AgentRunRegistry).
/// Optional per-ticket debounce prevents re-firing within a configurable window.
///
/// The debounce marker is only written to disk after the engine confirms the
/// dispatch via <see cref="CommitFiring"/> — firings skipped by transient gates
/// (concurrency, dedup, budget) are retried next poll.
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

        var debounce = _spec.DebounceSeconds;
        JsonObject? lastFiredNode = null;
        if (debounce > 0)
        {
            var state = ctx.Sessions.Load(ctx.WorkspacePath);
            lastFiredNode = GetLastFiredBucket(state, ctx.Automation.Id);
        }

        var firings = new List<TriggerFiring>();
        foreach (var col in _spec.Columns)
        {
            var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug, statusFilter: col);
            foreach (var t in tickets)
            {
                if (!string.IsNullOrEmpty(_spec.AssigneeSlug))
                {
                    if (t.AssignedTo is null || t.AssignedTo != _spec.AssigneeSlug) continue;
                }

                if (debounce > 0 && t.Id is { } tid)
                {
                    var lastIso = lastFiredNode?[tid.ToString()]?.GetValue<string>();
                    if (lastIso is not null &&
                        DateTime.TryParse(lastIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastFiredAt) &&
                        (ctx.Now - lastFiredAt).TotalSeconds < debounce)
                    {
                        continue;
                    }
                }

                firings.Add(new TriggerFiring(t.Id, t.Title, t.Status));
            }
        }

        return firings;
    }

    public Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null)
    {
        if (_spec.DebounceSeconds > 0 && firing.TicketId is int tid)
        {
            var state = ctx.Sessions.Load(ctx.WorkspacePath);
            var bucket = GetLastFiredBucket(state, ctx.Automation.Id);
            bucket[tid.ToString()] = (completedAt ?? ctx.Now).ToString("o");
            ctx.Sessions.Save(ctx.WorkspacePath, state);
        }
        return Task.CompletedTask;
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastEvaluated == DateTime.MinValue ? now : _lastEvaluated.AddSeconds(_spec.Seconds);

    private static JsonObject GetLastFiredBucket(JsonObject state, string autoId)
    {
        var autoNode = state[autoId] as JsonObject;
        if (autoNode is null)
        {
            autoNode = new JsonObject();
            state[autoId] = autoNode;
        }
        var bucket = autoNode["lastFired"] as JsonObject;
        if (bucket is null)
        {
            bucket = new JsonObject();
            autoNode["lastFired"] = bucket;
        }
        return bucket;
    }
}
