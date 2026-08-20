using System.Collections.Concurrent;

namespace KittyClaw.Core.Services;

/// <summary>
/// Prevents a terminal ticket worktree from being finalized while KittyClaw is
/// still running processor actions that may write to that worktree.
/// </summary>
public sealed class WorktreeFinalizationCoordinator
{
    private readonly ConcurrentDictionary<(string Slug, int RootTicketId), int> _writers = new();

    public IDisposable Enter(string projectSlug, int rootTicketId)
    {
        var key = (projectSlug.ToLowerInvariant(), rootTicketId);
        _writers.AddOrUpdate(key, 1, static (_, count) => count + 1);
        return new Lease(this, key);
    }

    public bool IsBusy(string projectSlug, int rootTicketId) =>
        _writers.ContainsKey((projectSlug.ToLowerInvariant(), rootTicketId));

    private void Exit((string Slug, int RootTicketId) key)
    {
        while (_writers.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                if (_writers.TryRemove(key, out _)) return;
            }
            else if (_writers.TryUpdate(key, count - 1, count)) return;
        }
    }

    private sealed class Lease(WorktreeFinalizationCoordinator owner, (string Slug, int RootTicketId) key) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Exit(key);
        }
    }
}
