using Microsoft.Data.Sqlite;

namespace KittyClaw.Core.Services;

/// <summary>
/// Global semaphore (size 1) that serializes all dashboard tile refreshes across all projects.
/// Scheduling policy: oldest lastFinishedAt first; never-run tiles get lowest priority (treated
/// as most recent so they don't jump the queue). Manual refreshes jump the priority queue but do
/// not preempt the currently-running tile. One slot per (projectSlug, tileSlug) -- re-queue is a no-op.
/// The dashboard_tile_runs table is created lazily on first use, not at construction time, to
/// avoid racing with EF Core's registry.db initialization.
/// </summary>
public sealed class DashboardTileGate : IDisposable
{
    public event Action? StateChanged;

    private readonly string _registryDbPath;
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly object _lock = new();

    private (string Slug, string TileSlug)? _running;
    private readonly LinkedList<QueueEntry> _queue = new();

    private record QueueEntry(string Slug, string TileSlug, bool Manual, TaskCompletionSource<bool> Tcs);

    public DashboardTileGate(ProjectService projectService)
    {
        _registryDbPath = Path.Combine(projectService.DataDir, "registry.db");
    }

    // -- Public API ---------------------------------------------------------------

    public async Task RunAsync(
        string slug, string tileSlug, bool manual,
        Func<CancellationToken, Task> work,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_running.HasValue && _running.Value.Slug == slug && _running.Value.TileSlug == tileSlug)
                return;
            foreach (var e in _queue)
                if (e.Slug == slug && e.TileSlug == tileSlug)
                    return;

            if (manual)
                InsertManual(new QueueEntry(slug, tileSlug, true, tcs));
            else
                _queue.AddLast(new QueueEntry(slug, tileSlug, false, tcs));
        }

        NotifyStateChanged();
        TryDispatchNext();

        using var reg = ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs);
        try { await tcs.Task.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (_lock) { RemoveFromQueue(slug, tileSlug); }
            NotifyStateChanged();
            throw;
        }

        try
        {
            lock (_lock) { _running = (slug, tileSlug); }
            NotifyStateChanged();
            await work(ct).ConfigureAwait(false);
        }
        finally
        {
            await PersistLastFinishedAtAsync(slug, tileSlug).ConfigureAwait(false);
            lock (_lock) { _running = null; }
            _sem.Release();
            NotifyStateChanged();
            TryDispatchNext();
        }
    }

    public ((string Slug, string TileSlug)? Running, IReadOnlyList<(string Slug, string TileSlug)> Queued) Snapshot()
    {
        lock (_lock)
        {
            var queued = _queue.Select(e => (e.Slug, e.TileSlug)).ToList();
            return (_running, queued);
        }
    }

    public void Dispose() => _sem.Dispose();

    // -- Internals ----------------------------------------------------------------

    private void InsertManual(QueueEntry entry)
    {
        var node = _queue.Last;
        while (node is not null && !node.Value.Manual)
            node = node.Previous;

        if (node is null)
            _queue.AddFirst(entry);
        else
            _queue.AddAfter(node, entry);
    }

    private void RemoveFromQueue(string slug, string tileSlug)
    {
        for (var n = _queue.First; n is not null; n = n.Next)
        {
            if (n.Value.Slug == slug && n.Value.TileSlug == tileSlug)
            {
                _queue.Remove(n);
                return;
            }
        }
    }

    private void TryDispatchNext()
    {
        while (true)
        {
            if (!_sem.Wait(0)) return;

            QueueEntry? winner;
            lock (_lock)
            {
                winner = PickNext();
                if (winner is null) { _sem.Release(); return; }
                _queue.Remove(_queue.First(e => e.Slug == winner.Slug && e.TileSlug == winner.TileSlug)!);
            }

            if (winner.Tcs.TrySetResult(true)) return;
        }
    }

    private QueueEntry? PickNext()
    {
        if (_queue.Count == 0) return null;

        var manuals = _queue.Where(e => e.Manual).ToList();
        var candidates = manuals.Count > 0 ? manuals : _queue.ToList();

        var times = LoadLastFinishedAt(candidates.Select(e => (e.Slug, e.TileSlug)));

        QueueEntry? best = null;
        DateTime bestTime = DateTime.MaxValue;
        int idx = 0, bestIdx = int.MaxValue;
        foreach (var e in candidates)
        {
            var t = times.TryGetValue($"{e.Slug}:{e.TileSlug}", out var v) ? v : DateTime.MaxValue;
            if (best is null || t < bestTime || (t == bestTime && idx < bestIdx))
            {
                best = e; bestTime = t; bestIdx = idx;
            }
            idx++;
        }
        return best;
    }

    private Dictionary<string, DateTime> LoadLastFinishedAt(IEnumerable<(string Slug, string TileSlug)> tiles)
    {
        var result = new Dictionary<string, DateTime>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_registryDbPath}");
            conn.Open();
            EnsureTable(conn);
            foreach (var (slug, tileSlug) in tiles)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT lastFinishedAt FROM dashboard_tile_runs WHERE slug=@s AND tileSlug=@t";
                cmd.Parameters.AddWithValue("@s", slug);
                cmd.Parameters.AddWithValue("@t", tileSlug);
                if (cmd.ExecuteScalar() is string s && DateTime.TryParse(s, out var dt))
                    result[$"{slug}:{tileSlug}"] = dt;
            }
        }
        catch { }
        return result;
    }

    private async Task PersistLastFinishedAtAsync(string slug, string tileSlug)
    {
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_registryDbPath}");
            await conn.OpenAsync();
            EnsureTable(conn);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dashboard_tile_runs (slug, tileSlug, lastFinishedAt)
                VALUES (@s, @t, @ts)
                ON CONFLICT(slug, tileSlug) DO UPDATE SET lastFinishedAt=excluded.lastFinishedAt
                """;
            cmd.Parameters.AddWithValue("@s", slug);
            cmd.Parameters.AddWithValue("@t", tileSlug);
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS dashboard_tile_runs (
                slug TEXT NOT NULL,
                tileSlug TEXT NOT NULL,
                lastFinishedAt TEXT NOT NULL,
                PRIMARY KEY(slug, tileSlug)
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
}