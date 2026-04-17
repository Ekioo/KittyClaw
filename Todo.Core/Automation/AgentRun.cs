using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Todo.Core.Automation;

public enum AgentRunStatus { Running, Completed, Failed, Stopped }

public sealed class AgentRun
{
    public required string RunId { get; init; }
    public required string ProjectSlug { get; init; }
    public required int? TicketId { get; init; }
    public required string AgentName { get; init; }
    public required string SkillFile { get; init; }
    public required string ConcurrencyGroup { get; init; }
    public required DateTime StartedAt { get; init; }
    public string? SessionId { get; set; }
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }

    private readonly object _logLock = new();
    private readonly LinkedList<StreamEvent> _buffer = new();
    private const int MaxBuffer = 500;

    public Channel<string> SteeringQueue { get; } = Channel.CreateUnbounded<string>();
    public CancellationTokenSource Cancellation { get; } = new();
    public event Action<StreamEvent>? OnEvent;

    public IReadOnlyList<StreamEvent> SnapshotBuffer()
    {
        lock (_logLock) return _buffer.ToList();
    }

    public void Push(StreamEvent ev)
    {
        lock (_logLock)
        {
            _buffer.AddLast(ev);
            while (_buffer.Count > MaxBuffer) _buffer.RemoveFirst();
        }
        OnEvent?.Invoke(ev);
    }
}

public sealed record StreamEvent(DateTime At, string Kind, string Text);

public sealed class AgentRunRegistry
{
    private readonly ConcurrentDictionary<string, AgentRun> _runs = new();

    public event Action<AgentRun>? OnRunStarted;
    public event Action<AgentRun>? OnRunEnded;

    public AgentRun Register(AgentRun run)
    {
        _runs[run.RunId] = run;
        OnRunStarted?.Invoke(run);
        return run;
    }

    public void Complete(string runId, AgentRunStatus status, int? exitCode)
    {
        if (!_runs.TryGetValue(runId, out var run)) return;
        run.Status = status;
        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = exitCode;
        OnRunEnded?.Invoke(run);
    }

    public AgentRun? Get(string runId) => _runs.TryGetValue(runId, out var r) ? r : null;

    public IEnumerable<AgentRun> ActiveForProject(string projectSlug) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> AllActive() =>
        _runs.Values.Where(r => r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> ActiveForTicket(string projectSlug, int ticketId) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.TicketId == ticketId && r.Status == AgentRunStatus.Running);

    public bool HasActiveInGroup(string projectSlug, string concurrencyGroup) =>
        _runs.Values.Any(r => r.ProjectSlug == projectSlug && r.ConcurrencyGroup == concurrencyGroup && r.Status == AgentRunStatus.Running);

    public bool HasActiveAny(string projectSlug, IEnumerable<string> concurrencyGroups)
    {
        var set = new HashSet<string>(concurrencyGroups);
        return _runs.Values.Any(r => r.ProjectSlug == projectSlug && set.Contains(r.ConcurrencyGroup) && r.Status == AgentRunStatus.Running);
    }

    public void Remove(string runId) => _runs.TryRemove(runId, out _);

    /// <summary>Purge runs that ended more than N minutes ago.</summary>
    public void PurgeOld(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        foreach (var r in _runs.Values.Where(r => r.Status != AgentRunStatus.Running && r.EndedAt is not null && r.EndedAt < cutoff).ToList())
            _runs.TryRemove(r.RunId, out _);
    }
}
