using System.Text.Json;
using System.Text.Json.Nodes;

namespace Todo.Core.Automation;

/// <summary>
/// Reads/writes .agents/channel/dispatch-state.json. Format preserved for
/// compatibility with the legacy dispatcher.mjs files (keys: _sessions,
/// _lastProcessedCommit, _ticketSnapshot, _learnedTickets, _committedTickets,
/// producer.lastSubStatuses, {agent}.lastDispatched).
/// </summary>
public sealed class SessionRegistry
{
    private readonly object _fileLock = new();

    private static string StatePath(string workspacePath) =>
        Path.Combine(workspacePath, ".agents", "channel", "dispatch-state.json");

    public JsonObject Load(string workspacePath)
    {
        var path = StatePath(workspacePath);
        lock (_fileLock)
        {
            if (!File.Exists(path)) return new JsonObject();
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : (JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
        }
    }

    public void Save(string workspacePath, JsonObject state)
    {
        var path = StatePath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (_fileLock)
        {
            File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public string? GetSessionId(string workspacePath, string agent, int ticketId)
    {
        var s = Load(workspacePath);
        var sessions = s["_sessions"] as JsonObject;
        return sessions?[$"{agent}:{ticketId}"]?.GetValue<string>();
    }

    public void SetSessionId(string workspacePath, string agent, int ticketId, string sessionId)
    {
        var s = Load(workspacePath);
        var sessions = s["_sessions"] as JsonObject ?? new JsonObject();
        sessions[$"{agent}:{ticketId}"] = sessionId;
        s["_sessions"] = sessions;
        Save(workspacePath, s);
    }

    public string? LastProcessedCommit(string workspacePath) =>
        Load(workspacePath)["_lastProcessedCommit"]?.GetValue<string>();

    public void SetLastProcessedCommit(string workspacePath, string sha)
    {
        var s = Load(workspacePath);
        s["_lastProcessedCommit"] = sha;
        Save(workspacePath, s);
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
        var s = Load(workspacePath);
        var obj = new JsonObject();
        foreach (var kv in snap) obj[kv.Key.ToString()] = kv.Value;
        s["_ticketSnapshot"] = obj;
        Save(workspacePath, s);
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
        var s = Load(workspacePath);
        var agentNode = s[agent] as JsonObject ?? new JsonObject();
        agentNode["lastDispatched"] = at.ToString("o");
        s[agent] = agentNode;
        Save(workspacePath, s);
    }
}
