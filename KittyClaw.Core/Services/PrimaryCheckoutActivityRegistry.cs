using System.Collections.Concurrent;

namespace KittyClaw.Core.Services;

/// <summary>
/// In-process registry of orchestrator-coordinated mutations of a primary checkout — today the
/// local-checkout synchronization step of the worktree merge queue. When the primary-repository
/// fingerprint drifts during a ticket run, <c>AgentRunner</c> consults this registry: drift that
/// overlaps a coordinated mutation window is reported as a KittyClaw-originated change instead of
/// being attributed to the agent. The registry is process-local, which matches the deployment
/// model: the synchronization step and agent runs are hosted by the same web process.
/// </summary>
public sealed class PrimaryCheckoutActivityRegistry
{
    private sealed class Entry
    {
        public int Active;
        public DateTime LastEndedUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a coordinated-mutation window for the repository; dispose to close it.</summary>
    public IDisposable BeginCoordinatedMutation(string repositoryPath)
    {
        var entry = _entries.GetOrAdd(Normalize(repositoryPath), _ => new Entry());
        lock (entry) entry.Active++;
        return new Scope(entry);
    }

    /// <summary>
    /// True when a coordinated mutation of the repository is still open, or closed at or after
    /// <paramref name="utcSince"/> — i.e. it may explain a state change observed since that instant.
    /// </summary>
    public bool HasCoordinatedMutationSince(string repositoryPath, DateTime utcSince)
    {
        if (!_entries.TryGetValue(Normalize(repositoryPath), out var entry)) return false;
        lock (entry) return entry.Active > 0 || entry.LastEndedUtc >= utcSince;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed class Scope(Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (entry)
            {
                entry.Active--;
                entry.LastEndedUtc = DateTime.UtcNow;
            }
        }
    }
}
