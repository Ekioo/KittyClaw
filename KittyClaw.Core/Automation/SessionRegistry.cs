using System.Text.Json;
using System.Text.Json.Nodes;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Reads/writes .agents/channel/dispatch-state.json. Format preserved for
/// compatibility with the legacy dispatcher.mjs files (keys: _sessions,
/// _lastProcessedCommit, _ticketSnapshot, _learnedTickets, _committedTickets,
/// producer.lastSubStatuses, {agent}.lastDispatched).
///
/// The concurrency gate allows several runs in the same workspace at once, so every
/// read-modify-write of the file must go through <see cref="Update"/> — a bare
/// Load → mutate → Save cycle loses whichever writer finishes first.
/// </summary>
public sealed class SessionRegistry
{
    private readonly object _fileLock = new();

    private static string StatePath(string workspacePath) =>
        Path.Combine(workspacePath, ".agents", "channel", "dispatch-state.json");

    public JsonObject Load(string workspacePath)
    {
        lock (_fileLock)
        {
            return LoadUnlocked(workspacePath);
        }
    }

    public void Save(string workspacePath, JsonObject state)
    {
        lock (_fileLock)
        {
            SaveUnlocked(workspacePath, state);
        }
    }

    /// <summary>Atomic read-modify-write: the lock is held across the whole cycle.</summary>
    public void Update(string workspacePath, Action<JsonObject> mutate)
    {
        lock (_fileLock)
        {
            var state = LoadUnlocked(workspacePath);
            mutate(state);
            SaveUnlocked(workspacePath, state);
        }
    }

    private static JsonObject LoadUnlocked(string workspacePath)
    {
        var path = StatePath(workspacePath);
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : (JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
    }

    private static void SaveUnlocked(string workspacePath, JsonObject state)
    {
        var path = StatePath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? GetSessionId(string workspacePath, string agent, int? ticketId)
    {
        var key = SessionKey(agent, ticketId);
        var s = Load(workspacePath);
        var sessions = s["_sessions"] as JsonObject;
        return sessions?[key]?.GetValue<string>();
    }

    public void SetSessionId(string workspacePath, string agent, int? ticketId, string sessionId)
    {
        var key = SessionKey(agent, ticketId);
        Update(workspacePath, s =>
        {
            var sessions = s["_sessions"] as JsonObject ?? new JsonObject();
            sessions[key] = sessionId;
            s["_sessions"] = sessions;
        });
    }

    public void Clear(string workspacePath, string agent, int? ticketId)
    {
        var key = SessionKey(agent, ticketId);
        Update(workspacePath, s =>
        {
            if (s["_sessions"] is JsonObject sessions)
                sessions.Remove(key);
        });
    }

    /// <summary>
    /// Session key identical to the legacy dispatcher.mjs: `{agent}:{ticketId}` when
    /// bound to a ticket, or `{agent}:sweep` for global/stateless agents like groomer.
    /// </summary>
    private static string SessionKey(string agent, int? ticketId) =>
        $"{agent}:{(ticketId?.ToString() ?? "sweep")}";

    public string? LastProcessedCommit(string workspacePath) =>
        Load(workspacePath)["_lastProcessedCommit"]?.GetValue<string>();

    public void SetLastProcessedCommit(string workspacePath, string sha)
    {
        Update(workspacePath, s => s["_lastProcessedCommit"] = sha);
    }

    public Dictionary<int, string> TicketSnapshot(string workspacePath)
    {
        var s = Load(workspacePath);
        var snap = s["_ticketSnapshot"] as JsonObject;
        var dict = new Dictionary<int, string>();
        if (snap is null) return dict;
        foreach (var kv in snap)
            if (int.TryParse(kv.Key, out var id) && kv.Value is not null)
                dict[id] = kv.Value.GetValue<string>();
        return dict;
    }

    public void SaveTicketSnapshot(string workspacePath, IReadOnlyDictionary<int, string> snap)
    {
        Update(workspacePath, s =>
        {
            var obj = new JsonObject();
            foreach (var kv in snap) obj[kv.Key.ToString()] = kv.Value;
            s["_ticketSnapshot"] = obj;
        });
    }

    public DateTime? LastDispatched(string workspacePath, string agent)
    {
        var s = Load(workspacePath);
        var agentNode = s[agent] as JsonObject;
        var iso = agentNode?["lastDispatched"]?.GetValue<string>();
        return iso is null ? null : DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    public void SetLastDispatched(string workspacePath, string agent, DateTime at)
    {
        Update(workspacePath, s =>
        {
            var agentNode = s[agent] as JsonObject ?? new JsonObject();
            agentNode["lastDispatched"] = at.ToString("o");
            s[agent] = agentNode;
        });
    }
}
