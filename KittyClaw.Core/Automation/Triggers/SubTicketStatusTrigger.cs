using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Automation.Triggers;

/// <summary>
/// Fires on a parent ticket when the CSV of its sub-ticket statuses changes
/// compared to the previously recorded one. Reproduces Lain's
/// `producer.lastSubStatuses` gating to avoid re-dispatching the producer while
/// nothing actionable has changed in the sub-tree.
///
/// The CSV marker is only persisted after the engine confirms the dispatch via
/// <see cref="CommitFiringAsync"/> — firings skipped by transient gates are retried.
///
/// Event-driven path: <see cref="TryHandleExternalSignal"/> queues incoming
/// <see cref="StatusChangeSignal"/> items; <see cref="EvaluateAsync"/> drains the
/// queue on every call (bypassing the poll debounce) so changes are detected within
/// one engine tick (&lt;1 s) rather than waiting for the next full poll cycle.
/// </summary>
public sealed class SubTicketStatusTrigger : ITrigger
{
    private DateTime _lastPolled = DateTime.MinValue;
    private readonly SubTicketStatusTriggerSpec _spec;

    // Child ticket IDs whose status just changed — queued by TryHandleExternalSignal,
    // drained synchronously inside EvaluateAsync.
    private readonly ConcurrentQueue<int> _pendingChildIds = new();

    public SubTicketStatusTrigger(SubTicketStatusTriggerSpec spec) { _spec = spec; }

    public bool TryHandleExternalSignal(object signal, out IReadOnlyList<TriggerFiring> firings)
    {
        firings = Array.Empty<TriggerFiring>();
        if (signal is not StatusChangeSignal s) return false;

        // Queue the child ID for processing in the next EvaluateAsync tick.
        _pendingChildIds.Enqueue(s.TicketId);
        // Return false: we cannot resolve the parent synchronously here.
        // EvaluateAsync will drain the queue and produce the real firings.
        return false;
    }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        var hasPending = !_pendingChildIds.IsEmpty;

        // Skip regular poll debounce only when there are pending signal-driven child IDs.
        if (!hasPending && (ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Array.Empty<TriggerFiring>();
        _lastPolled = ctx.Now;

        // Collect parent IDs to (re-)evaluate: signal-driven ones + all parents from full poll.
        var parentIdsToCheck = new HashSet<int>();

        // Drain queued child IDs and resolve their parents.
        while (_pendingChildIds.TryDequeue(out var childId))
        {
            var child = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, childId);
            if (child?.ParentId is int pid)
                parentIdsToCheck.Add(pid);
        }

        var parents = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug,
            statusFilter: _spec.ParentColumn);

        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        var agentKey = ctx.Automation.Id;
        var bucket = state[agentKey] as JsonObject ?? new JsonObject();
        var lastSubs = bucket["lastSubStatuses"] as JsonObject ?? new JsonObject();

        // When signal-driven: only re-evaluate the parents whose children changed.
        // When poll-driven: evaluate all parents in the target column.
        var parentsToEval = parentIdsToCheck.Count > 0
            ? parents.Where(p => parentIdsToCheck.Contains(p.Id))
            : parents;

        var firings = new List<TriggerFiring>();
        foreach (var parent in parentsToEval)
        {
            if (parent.SubTickets.Count == 0) continue;

            var csv = ComputeCsv(parent.SubTickets);
            var prev = lastSubs[parent.Id.ToString()]?.GetValue<string>();
            if (prev == csv) continue;

            if (_spec.DebounceSeconds is not null)
            {
                var lastAt = ctx.Sessions.LastDispatched(ctx.WorkspacePath, agentKey);
                if (lastAt is not null && (ctx.Now - lastAt.Value).TotalSeconds < _spec.DebounceSeconds.Value)
                    continue;
            }

            firings.Add(new TriggerFiring(parent.Id, parent.Title, parent.Status));
        }

        return firings;
    }

    public async Task CommitFiringAsync(TriggerContext ctx, TriggerFiring firing, DateTime? completedAt = null)
    {
        if (firing.TicketId is not int tid) return;
        var ticket = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, tid);
        if (ticket is null) return;
        var csv = ComputeCsv(ticket.SubTickets);

        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        var agentKey = ctx.Automation.Id;
        var bucket = state[agentKey] as JsonObject;
        if (bucket is null) { bucket = new JsonObject(); state[agentKey] = bucket; }
        var lastSubs = bucket["lastSubStatuses"] as JsonObject;
        if (lastSubs is null) { lastSubs = new JsonObject(); bucket["lastSubStatuses"] = lastSubs; }
        lastSubs[tid.ToString()] = csv;
        ctx.Sessions.Save(ctx.WorkspacePath, state);
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);

    private static string ComputeCsv(IEnumerable<SubTicketInfo> subs) =>
        string.Join(",", subs.OrderBy(s => s.Id).Select(s => $"{s.Id}:{s.Status}"));
}
