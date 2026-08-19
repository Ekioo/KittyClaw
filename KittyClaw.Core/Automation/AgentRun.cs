using System.Collections.Concurrent;
using System.Text;
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
    /// <summary>Image references accepted for this run. Retained for QA observability after best-effort file cleanup.</summary>
    public IReadOnlyList<string> InputImagePaths { get; init; } = [];
    /// <summary>Content hashes captured before temporary image cleanup, used for ordered diagnostics.</summary>
    public IReadOnlyList<string> InputImageHashes { get; init; } = [];
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public CliVersionMetadata? CliVersion { get; set; }

    /// <summary>
    /// True when the final subprocess attempt failed with a quota / spend / rate-limit signal
    /// (including after a fallback retry). Used by <c>restoreStatusOnFail</c> to park the ticket
    /// in Blocked instead of restoring Todo and re-dispatching every poll.
    /// </summary>
    public bool HitQuota { get; set; }

    // Token usage accumulated from the claude CLI's terminal `result` events. A single AgentRun
    // can spawn several subprocesses (resume retry, quota fallback, chat steer replay), each
    // emitting its own result event, so these are sums — not the last event's values.
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadTokens { get; private set; }
    public int CacheWriteTokens { get; private set; }
    public decimal? TotalCostUsd { get; private set; }
    public bool CostIsEstimated { get; private set; }
    public long TotalTokens => (long)InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
    public bool HasUsage => TotalTokens > 0 || TotalCostUsd is not null;

    public void AddUsage(int inputTokens, int outputTokens, int cacheReadTokens, int cacheWriteTokens,
        decimal? costUsd, bool costIsEstimated = false)
    {
        lock (_logLock)
        {
            InputTokens += inputTokens;
            OutputTokens += outputTokens;
            CacheReadTokens += cacheReadTokens;
            CacheWriteTokens += cacheWriteTokens;
            if (costUsd is not null)
            {
                TotalCostUsd = (TotalCostUsd ?? 0m) + costUsd.Value;
                CostIsEstimated |= costIsEstimated;
            }
        }
    }

    /// <summary>Minutes of inactivity after which the concurrency-lock reaper force-releases this run's
    /// group (dead man's switch). Null disables the timeout. Set from the automation's
    /// <c>lockTimeoutMinutes</c> via <see cref="AgentRunContext.LockTimeoutMinutes"/>.</summary>
    public int? LockTimeoutMinutes { get; init; }

    /// <summary>UTC timestamp of the last StreamEvent pushed onto this run — its heartbeat. Updated on
    /// every <see cref="Push"/>. The reaper compares <c>now - LastActivityAt</c> against
    /// <see cref="LockTimeoutMinutes"/> to detect a hung run whose subprocess stopped emitting.</summary>
    public DateTime LastActivityAt { get; private set; } = DateTime.UtcNow;

    private readonly object _logLock = new();
    private readonly LinkedList<StreamEvent> _buffer = new();
    private const int MaxBuffer = 500;

    public Channel<string> SteeringQueue { get; } = Channel.CreateUnbounded<string>();
    public CancellationTokenSource Cancellation { get; } = new();
    public bool IsAwaitingUserAnswer { get; set; }
    private readonly object _approvalLock = new();
    private string? _awaitingApprovalRequestId;
    private TaskCompletionSource? _approvalResolution;
    internal event Action<bool>? ApprovalWaitChanged;
    public string? AwaitingApprovalRequestId
    {
        get { lock (_approvalLock) return _awaitingApprovalRequestId; }
    }
    public event Action<StreamEvent>? OnEvent;

    public bool TryPauseForApproval(string requestId)
    {
        lock (_approvalLock)
        {
            if (_awaitingApprovalRequestId is not null) return false;
            _awaitingApprovalRequestId = requestId;
            _approvalResolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        ApprovalWaitChanged?.Invoke(true);
        return true;
    }

    public bool TryResumeFromApproval(string requestId)
    {
        TaskCompletionSource? resolution;
        lock (_approvalLock)
        {
            if (_awaitingApprovalRequestId != requestId) return false;
            _awaitingApprovalRequestId = null;
            resolution = _approvalResolution;
            _approvalResolution = null;
        }
        ApprovalWaitChanged?.Invoke(false);
        resolution?.TrySetResult();
        return true;
    }

    public Task WaitForApprovalResolutionAsync(CancellationToken ct)
    {
        lock (_approvalLock)
            return _approvalResolution?.Task.WaitAsync(ct) ?? Task.CompletedTask;
    }

    private readonly List<string> _pendingSteerMessages = new();
    public IReadOnlyList<string> PendingSteerMessages => _pendingSteerMessages;

    // Grok (and any token-streamed provider) assembles assistant text across many small
    // content_block_delta events; the final assistant/result event drains this buffer so
    // chat history gets one complete message instead of hundreds of single-token rows.
    private readonly StringBuilder _streamText = new();

    public void AppendStreamText(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        lock (_logLock) _streamText.Append(chunk);
    }

    /// <summary>Returns the accumulated stream text and clears the buffer.</summary>
    public string TakeStreamText()
    {
        lock (_logLock)
        {
            var s = _streamText.ToString();
            _streamText.Clear();
            return s;
        }
    }

    public void AddPendingSteerMessage(string msg)
    {
        lock (_logLock) _pendingSteerMessages.Add(msg);
    }

    public IReadOnlyList<string> DrainPendingSteerMessages()
    {
        lock (_logLock)
        {
            var result = _pendingSteerMessages.ToList();
            _pendingSteerMessages.Clear();
            return result;
        }
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
            LastActivityAt = ev.At;
        }
        OnEvent?.Invoke(ev);
    }
}

public sealed record StreamEvent(
    DateTime At,
    string Kind,
    string Text,
    string? Detail = null,
    string? CorrelationId = null);

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
    public string? ChatTarget { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentRunStatus Status { get; set; }
    public int? ExitCode { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    public decimal? TotalCostUsd { get; set; }
    public bool CostIsEstimated { get; set; }
    public CliVersionMetadata? CliVersion { get; set; }
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
            ChatTarget = run.ChatTarget,
            Status = run.Status,
            ExitCode = run.ExitCode,
            InputTokens = run.InputTokens,
            OutputTokens = run.OutputTokens,
            CacheReadTokens = run.CacheReadTokens,
            CacheWriteTokens = run.CacheWriteTokens,
            TotalCostUsd = run.TotalCostUsd,
            CostIsEstimated = run.CostIsEstimated,
            CliVersion = run.CliVersion,
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
            run.ChatTarget = snapshot.ChatTarget;
            run.Status = snapshot.Status;
            run.EndedAt = snapshot.EndedAt;
            run.ExitCode = snapshot.ExitCode;
            run.CliVersion = snapshot.CliVersion;
            run.AddUsage(snapshot.InputTokens, snapshot.OutputTokens,
                snapshot.CacheReadTokens, snapshot.CacheWriteTokens, snapshot.TotalCostUsd,
                snapshot.CostIsEstimated);
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
    private readonly ConcurrentDictionary<(string ProjectSlug, int TicketId), ConcurrentDictionary<string, AgentRun>> _runsByTicket = new();
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
                if (!string.IsNullOrWhiteSpace(run.ChatTarget))
                {
                    run.Push(new StreamEvent(DateTime.UtcNow, "interrupted",
                        "KittyClaw restarted while this chat session was running."));
                }
                run.Status = AgentRunStatus.Stopped;
                run.EndedAt = DateTime.UtcNow;
                store.Save(run);
            }
            _runs[run.RunId] = run;
            Index(run);
        }
    }

    public AgentRun Register(AgentRun run)
    {
        if (_runs.TryGetValue(run.RunId, out var previous))
            Unindex(previous);
        _runs[run.RunId] = run;
        Index(run);
        _store?.Save(run);
        OnRunStarted?.Invoke(run);
        return run;
    }

    public void Persist(AgentRun run) => _store?.Save(run);

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

    /// <summary>All currently-Running runs across every project. Used by the concurrency-lock reaper.</summary>
    public IEnumerable<AgentRun> AllActive() =>
        _runs.Values.Where(r => r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> ActiveForTicket(string projectSlug, int ticketId) =>
        RunsForTicket(projectSlug, ticketId).Where(r => r.Status == AgentRunStatus.Running);

    public IEnumerable<AgentRun> AllForTicket(string projectSlug, int ticketId) =>
        RunsForTicket(projectSlug, ticketId);

    public IEnumerable<AgentRun> AllForProject(string projectSlug) =>
        _runs.Values.Where(r => r.ProjectSlug == projectSlug);

    public bool HasActiveInGroup(string projectSlug, string concurrencyGroup) =>
        _runs.Values.Any(r => r.ProjectSlug == projectSlug && r.ConcurrencyGroup == concurrencyGroup && r.Status == AgentRunStatus.Running);

    public bool HasActiveAny(string projectSlug, IEnumerable<string> concurrencyGroups)
    {
        var set = new HashSet<string>(concurrencyGroups);
        return _runs.Values.Any(r => r.ProjectSlug == projectSlug && set.Contains(r.ConcurrencyGroup) && r.Status == AgentRunStatus.Running);
    }

    public void Remove(string runId)
    {
        if (_runs.TryRemove(runId, out var run))
            Unindex(run);
    }

    public AgentRun? LastCompletedForChatTarget(string projectSlug, string chatTarget) =>
        _runs.Values
            .Where(r => r.ProjectSlug == projectSlug && r.ChatTarget == chatTarget && r.Status != AgentRunStatus.Running && r.EndedAt is not null)
            .MaxBy(r => r.EndedAt);

    public AgentRun? LastInterruptedForChatTarget(string projectSlug, string chatTarget)
    {
        var latest = _runs.Values
            .Where(r => r.ProjectSlug == projectSlug && r.ChatTarget == chatTarget)
            .MaxBy(r => r.StartedAt);
        return latest is not null
            && latest.Status == AgentRunStatus.Stopped
            && latest.SnapshotBuffer().Any(e => e.Kind == "interrupted")
                ? latest
                : null;
    }

    public IReadOnlyList<AgentRun> InterruptedChats() =>
        _runs.Values
            .Where(r => !string.IsNullOrWhiteSpace(r.ChatTarget))
            .GroupBy(r => (r.ProjectSlug, r.ChatTarget))
            .Select(group => group.MaxBy(r => r.StartedAt))
            .Where(r => r is not null
                && r.Status == AgentRunStatus.Stopped
                && r.SnapshotBuffer().Any(e => e.Kind == "interrupted"))
            .Cast<AgentRun>()
            .ToList();

    /// <summary>Purge runs that ended more than N minutes ago.</summary>
    public void PurgeOld(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        foreach (var r in _runs.Values.Where(r => r.Status != AgentRunStatus.Running && r.EndedAt is not null && r.EndedAt < cutoff).ToList())
        {
            if (_runs.TryRemove(r.RunId, out var removed))
            {
                Unindex(removed);
                _store?.Delete(r.RunId);
            }
        }
    }

    private IEnumerable<AgentRun> RunsForTicket(string projectSlug, int ticketId) =>
        _runsByTicket.TryGetValue((projectSlug, ticketId), out var runs)
            ? runs.Values
            : [];

    private void Index(AgentRun run)
    {
        if (run.TicketId is not int ticketId) return;
        var runs = _runsByTicket.GetOrAdd((run.ProjectSlug, ticketId), _ => new());
        runs[run.RunId] = run;
    }

    private void Unindex(AgentRun run)
    {
        if (run.TicketId is not int ticketId) return;
        var key = (run.ProjectSlug, ticketId);
        if (!_runsByTicket.TryGetValue(key, out var runs)) return;
        runs.TryRemove(run.RunId, out _);
    }
}
