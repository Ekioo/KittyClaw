namespace KittyClaw.Core.Automation;

/// <summary>
/// Caps the number of simultaneously-running claude subprocesses across all projects so the
/// host doesn't get OOM-killed when many automations fire at once. Chats bypass the gate
/// entirely so the user-facing experience always feels responsive.
///
/// Scheduling is "least-recently-started first": when a slot frees and several automations
/// are queued, the one whose agent slug ran the longest ago (or never) is promoted, with
/// arrival order as tiebreaker. This prevents a chatty agent from monopolising the slots.
/// </summary>
public sealed class RunConcurrencyGate
{
    public int MaxConcurrent { get; }

    private readonly object _lock = new();
    private int _active;
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly Dictionary<string, DateTime> _lastStarted = new();
    private long _arrivalSeq;

    private sealed record Waiter(string AgentName, TaskCompletionSource<bool> Tcs, long ArrivalOrder);

    public RunConcurrencyGate(int maxConcurrent = 3)
    {
        MaxConcurrent = Math.Max(1, maxConcurrent);
    }

    public (int Active, int Queued, int Max) Snapshot()
    {
        lock (_lock) return (_active, _waiters.Count, MaxConcurrent);
    }

    /// <summary>
    /// Acquire a slot. Chat runs return a no-op handle immediately. Automation runs await a
    /// free slot; the runner picks the queued agent whose last start is furthest in the past
    /// (fair scheduling). Cancellation surfaces as <see cref="OperationCanceledException"/>
    /// and removes the waiter from the queue without consuming a slot.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(bool isChat, string agentName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (isChat)
        {
            // Chats bypass slot accounting but still count as "recent activity" for fairness:
            // a chatty agent should not jump the queue ahead of one that hasn't run in a while.
            lock (_lock) { _lastStarted[agentName] = DateTime.UtcNow; }
            return Releaser.Noop;
        }

        TaskCompletionSource<bool> tcs;
        LinkedListNode<Waiter> node;
        lock (_lock)
        {
            if (_active < MaxConcurrent)
            {
                _active++;
                _lastStarted[agentName] = DateTime.UtcNow;
                return new Releaser(this);
            }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(new Waiter(agentName, tcs, ++_arrivalSeq));
        }

        using (ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs))
        {
            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                // If we got cancelled before promotion, remove ourselves so Release doesn't
                // spin promoting a cancelled waiter (Release is also defensive against this,
                // but cleaning up keeps Snapshot accurate).
                lock (_lock)
                {
                    if (node.List == _waiters) _waiters.Remove(node);
                }
            }
        }
        return new Releaser(this);
    }

    private void Release()
    {
        // Loop in case the picked waiter was already cancelled — try the next one. The slot
        // stays "ours" until promotion succeeds or the queue empties.
        while (true)
        {
            Waiter? winner = null;
            LinkedListNode<Waiter>? winnerNode = null;
            lock (_lock)
            {
                var bestStamp = DateTime.MaxValue;
                for (var n = _waiters.First; n is not null; n = n.Next)
                {
                    var stamp = _lastStarted.TryGetValue(n.Value.AgentName, out var t) ? t : DateTime.MinValue;
                    if (winnerNode is null
                        || stamp < bestStamp
                        || (stamp == bestStamp && n.Value.ArrivalOrder < winner!.ArrivalOrder))
                    {
                        winner = n.Value;
                        winnerNode = n;
                        bestStamp = stamp;
                    }
                }
                if (winner is null) { _active--; return; }
                _waiters.Remove(winnerNode!);
            }

            if (winner.Tcs.TrySetResult(true))
            {
                lock (_lock) { _lastStarted[winner.AgentName] = DateTime.UtcNow; }
                return;
            }
            // candidate was cancelled — loop and pick another
        }
    }

    private sealed class Releaser : IDisposable
    {
        public static readonly Releaser Noop = new(null);
        private RunConcurrencyGate? _gate;
        public Releaser(RunConcurrencyGate? gate) => _gate = gate;
        public void Dispose()
        {
            var g = Interlocked.Exchange(ref _gate, null);
            g?.Release();
        }
    }
}
