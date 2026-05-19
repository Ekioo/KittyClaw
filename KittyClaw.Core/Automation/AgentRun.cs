using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace KittyClaw.Core.Automation;

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
    public string? Model { get; set; }
    public string? ChatTarget { get; set; }
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }

    private readonly object _logLock = new();
    private readonly LinkedList<StreamEvent> _buffer = new();
    private const int MaxBuffer = 500;

    public Channel<string> SteeringQueue { get; } = Channel.CreateUnbounded<string>();
    public CancellationTokenSource Cancellation { get; } = new();
    public event Action<StreamEvent>? OnEvent;

    private readonly List<string> _pendingSteerMessages = new();
    public IReadOnlyList<string> PendingSteerMessages => _pendingSteerMessages;

    public void AddPendingSteerMessage(string msg)
    {
        lock (_logLock) _pendingSteerMessages.Add(msg);
    }

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

public sealed record StreamEvent(DateTime At, string Kind, string Text, string? Detail = null);

/// <summary>Serializable snapshot of a completed AgentRun for disk persistence.</summary>
public sealed class AgentRunSnapshot
{
    public string RunId { get; set; } = "";
    public string ProjectSlug { get; set; } = "";
    public int? TicketId { get; set; }
    public string AgentName { get; set; } = "";
    public string SkillFile { get; set; } = "";
    public string ConcurrencyGroup { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentRunStatus Status { get; set; }
    public int? ExitCode { get; set; }
    public List<StreamEvent> Events { get; set; } = [];
    public List<string> PendingSteerMessages { get; set; } = [];
}

/// <summary>Persists completed runs as JSON files on disk.</summary>
public sealed class RunLogStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RunLogStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "runs");
        Directory.CreateDirectory(_dir);
    }

    public void Save(AgentRun run)
    {
        var snapshot = new AgentRunSnapshot
        {
            RunId = run.RunId,
            ProjectSlug = run.ProjectSlug,
            TicketId = run.TicketId,
            AgentName = run.AgentName,
            SkillFile = run.SkillFile,
            ConcurrencyGroup = run.ConcurrencyGroup,
            StartedAt = run.StartedAt,
            EndedAt = run.EndedAt,
            SessionId = run.SessionId,
            Model = run.Model,
            Status = run.Status,
            ExitCode = run.ExitCode,
            Events = run.SnapshotBuffer().ToList(),
            PendingSteerMessages = run.PendingSteerMessages.ToList(),
        };
        var path = Path.Combine(_dir, $"{run.RunId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, s_json));
    }

    public void Delete(string runId)
    {
        var path = Path.Combine(_dir, $"{runId}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public IEnumerable<AgentRun> LoadAll()
    {
        if (!Directory.Exists(_dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            AgentRunSnapshot? snapshot;
            try
            {
                var json = File.ReadAllText(file);
                snapshot = JsonSerializer.Deserialize<AgentRunSnapshot>(json, s_json);
            }
            catch { continue; }
            if (snapshot is null) continue;

            var run = new AgentRun
            {
                RunId = snapshot.RunId,
                ProjectSlug = snapshot.ProjectSlug,
                TicketId = snapshot.TicketId,
                AgentName = snapshot.AgentName,
                SkillFile = snapshot.SkillFile,
                ConcurrencyGroup = snapshot.ConcurrencyGroup,
                StartedAt = snapshot.StartedAt,
            };
            run.SessionId = snapshot.SessionId;
            run.Model = snapshot.Model;
            run.Status = snapshot.Status;
            run.EndedAt = snapshot.EndedAt;
            run.ExitCode = snapshot.ExitCode;
            foreach (var ev in snapshot.Events)
                run.Push(ev);
            foreach (var msg in snapshot.PendingSteerMessages)
                run.AddPendingSteerMessage(msg);
            yield return run;
        }
    }
}

public sealed class AgentRunRegistry
{
    private readonly ConcurrentDictionary<string, AgentRun> _runs = new();
    private readonly RunLogStore? _store;

    public event Action<AgentRun>? OnRunStarted;
    public event Action<AgentRun>? OnRunEnded;

    public AgentRunRegistry() { }

    public AgentRunRegistry(RunLogStore store)
    {
        _store = store;
        foreach (var run in store.LoadAll())
        {
            // A live run is never persisted — any snapshot with Status=Running is stale
            // (process exited before Complete was called). Reconcile to Stopped so the UI
            // never shows permanently-Running runs after a restart.
            if (run.Status == AgentRunStatus.Running)
            {
                run.Status = AgentRunStatus.Stopped;
                run.EndedAt = DateTime.UtcNow;
                store.Save(run);
            }
            _runs[run.RunId] = run;
        }
    }

    public AgentRun Register(AgentRun run)
    {
        _runs[run.RunId] = run;
        OnRunStarted?.Invoke(run);
        return run;
    }

    public void Complete(string runId, AgentRunStatus status, int? exitCode)
    {
        if (!_runs.TryGetValue(runId, out var run)) return;
        // Idempotent: a terminal status must never be downgraded by a stray second call.
        if (run.Status != AgentRunStatus.Running) return;
        run.Status = status;
        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = exitCode;
        _store?.Save(run);
        OnRunEnded?.Invoke(run);
    }

    public AgentRun? Get(string runId) => _runs.TryGetValue(runId, out var r) ? r : null;

    public IEnumerable<AgentRun> ActiveForProject(string projectSlug) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> ActiveForTicket(string projectSlug, int ticketId) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.TicketId == ticketId && r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> AllForTicket(string projectSlug, int ticketId) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug && r.TicketId == ticketId);

    public IEnumerable<AgentRun> AllForProject(string projectSlug) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug);

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
        {
            _runs.TryRemove(r.RunId, out _);
            _store?.Delete(r.RunId);
        }
    }
}
