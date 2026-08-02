using System.Text.Json.Nodes;

namespace KittyClaw.Core.Automation.Triggers;

/// <summary>Signal emitted by TicketService when a comment is added to a ticket.</summary>
public sealed record CommentAddedSignal(int TicketId, int CommentId, string Author, string Content);

/// <summary>
/// Fires when a new comment is added to any ticket, optionally filtered by author.
///
/// Dedup (ticket #113 — the same comment used to re-fire on every poll, up to 8 phantom agent
/// runs in production):
/// - The last consumed comment ID is persisted PER AUTOMATION and per ticket under
///   "_lastCommentIdsByAutomation" in dispatch-state.json. The old shared flat map meant one
///   automation's scan hid comments from (or rolled back) another's.
/// - Persistence is a single atomic monotonic merge (per-key max) inside SessionRegistry.Update,
///   never a whole-object overwrite: the old Load → await… → Save cycle could write a stale
///   snapshot over a newer one and resurrect already-consumed comments.
/// - Comments dispatched through the urgent signal path are consumed via
///   <see cref="ConsumeSignalFiringAsync"/> — called by the engine AFTER conditions pass,
///   just before dispatch, and persisted (ticket #136). An event whose conditions fail at
///   signal time therefore stays unconsumed and gets one retry from the next poll (whose
///   eager cursor advance then consumes it — the deliberate #113 anti-loop tradeoff).
/// - A first scan with no persisted state at all (neither the per-automation bucket nor the
///   legacy "_lastCommentIds" flat map) seeds silently: it records the current max IDs without
///   firing, instead of replaying the board's whole comment history. The legacy flat map, when
///   present, seeds the automation's bucket so existing installs migrate without a refire wave.
/// </summary>
public sealed class TicketCommentAddedTrigger : ITrigger
{
    private const string StateKey = "_lastCommentIdsByAutomation";
    private const string LegacyStateKey = "_lastCommentIds";

    private DateTime _lastPolled = DateTime.MinValue;
    private readonly TicketCommentAddedTriggerSpec _spec;
    // Comment IDs seen by TryHandleExternalSignal, per ticket — recorded only, NOT consumed:
    // consumption happens in ConsumeSignalFiringAsync once the dispatch is actually happening.
    private readonly Dictionary<int, int> _pendingSignal = new();

    public TicketCommentAddedTrigger(TicketCommentAddedTriggerSpec spec) { _spec = spec; }

    public async Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
    {
        if ((ctx.Now - _lastPolled).TotalSeconds < _spec.PollSeconds)
            return Array.Empty<TriggerFiring>();
        _lastPolled = ctx.Now;

        var (lastSeen, hasState) = LoadLastCommentIds(ctx);
        var tickets = await ctx.Tickets.ListTicketsAsync(ctx.ProjectSlug);
        var ticketsById = tickets.ToDictionary(ticket => ticket.Id);
        var commentsByTicket = (await ctx.Tickets.ListCommentCursorsAsync(ctx.ProjectSlug))
            .GroupBy(comment => comment.TicketId);
        var firings = new List<TriggerFiring>();
        var advanced = false;

        foreach (var comments in commentsByTicket)
        {
            if (!ticketsById.TryGetValue(comments.Key, out var ticket)) continue;

            lastSeen.TryGetValue(ticket.Id, out var prevMaxId);

            if (hasState && comments.Any(comment =>
                    comment.CommentId > prevMaxId &&
                    (_spec.Authors.Count == 0 || _spec.Authors.Contains(
                        comment.Author, StringComparer.OrdinalIgnoreCase))))
            {
                firings.Add(new TriggerFiring(ticket.Id, ticket.Title, ticket.Status));
            }

            var maxId = comments.Max(comment => comment.CommentId);
            if (maxId > prevMaxId)
            {
                lastSeen[ticket.Id] = maxId;
                advanced = true;
            }
        }

        // First scan must persist even an empty bucket so the NEXT scan knows state exists and
        // starts firing on genuinely new comments.
        if (advanced || !hasState)
            PersistMonotonic(ctx, lastSeen);

        return firings;
    }

    private static (Dictionary<int, int> LastSeen, bool HasState) LoadLastCommentIds(TriggerContext ctx)
    {
        var state = ctx.Sessions.Load(ctx.WorkspacePath);
        var node = (state[StateKey] as JsonObject)?[ctx.Automation.Id] as JsonObject;
        // Migration: automations without a bucket yet seed from the legacy shared flat map so
        // already-consumed comments stay consumed.
        node ??= state[LegacyStateKey] as JsonObject;
        var dict = new Dictionary<int, int>();
        if (node is null) return (dict, false);
        foreach (var kv in node)
            if (int.TryParse(kv.Key, out var ticketId) && kv.Value is not null)
                dict[ticketId] = kv.Value.GetValue<int>();
        return (dict, true);
    }

    /// <summary>Atomic per-key max-merge into this automation's bucket: a concurrent or stale
    /// writer can only ever raise a ticket's consumed ID, never roll it back.</summary>
    private void PersistMonotonic(TriggerContext ctx, Dictionary<int, int> lastSeen)
    {
        ctx.Sessions.Update(ctx.WorkspacePath, state =>
        {
            var byAutomation = state[StateKey] as JsonObject;
            if (byAutomation is null)
            {
                byAutomation = new JsonObject();
                state[StateKey] = byAutomation;
            }
            var bucket = byAutomation[ctx.Automation.Id] as JsonObject;
            if (bucket is null)
            {
                bucket = new JsonObject();
                byAutomation[ctx.Automation.Id] = bucket;
            }
            foreach (var kv in lastSeen)
            {
                var key = kv.Key.ToString();
                var existing = bucket[key]?.GetValue<int>() ?? 0;
                if (kv.Value > existing) bucket[key] = kv.Value;
            }
        });
    }

    public bool TryHandleExternalSignal(object signal, out IReadOnlyList<TriggerFiring> firings)
    {
        if (signal is not CommentAddedSignal s)
        {
            firings = Array.Empty<TriggerFiring>();
            return false;
        }

        var matches = _spec.Authors.Count == 0
                   || _spec.Authors.Contains(s.Author, StringComparer.OrdinalIgnoreCase);

        if (!matches)
        {
            firings = Array.Empty<TriggerFiring>();
            return false;
        }

        // Record only — the event is consumed in ConsumeSignalFiringAsync when it actually
        // dispatches. Marking it consumed here would silently drop a comment whose conditions
        // fail at this instant (ticket #136).
        _pendingSignal.TryGetValue(s.TicketId, out var prev);
        if (s.CommentId > prev) _pendingSignal[s.TicketId] = s.CommentId;

        firings = [new TriggerFiring(s.TicketId, null, null)];
        return true;
    }

    /// <summary>
    /// Persists the signaled comment as consumed (monotonic merge), so neither the next poll
    /// nor a reload re-fires it. Falls back to the ticket's current max comment ID when the
    /// pending entry was lost (e.g. a reload swapped the trigger instance between signal and
    /// dispatch).
    /// </summary>
    public async Task ConsumeSignalFiringAsync(TriggerContext ctx, TriggerFiring firing)
    {
        if (firing.TicketId is not int ticketId) return;
        if (!_pendingSignal.Remove(ticketId, out var commentId))
        {
            var ticket = await ctx.Tickets.GetTicketAsync(ctx.ProjectSlug, ticketId);
            if (ticket is null || ticket.Comments.Count == 0) return;
            commentId = ticket.Comments.Max(c => c.Id);
        }
        PersistMonotonic(ctx, new Dictionary<int, int> { [ticketId] = commentId });
    }

    public DateTime? GetNextRunAt(DateTime now) =>
        _lastPolled == DateTime.MinValue ? now : _lastPolled.AddSeconds(_spec.PollSeconds);
}
