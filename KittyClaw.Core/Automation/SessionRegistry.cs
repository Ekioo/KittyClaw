using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

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
    private const int WriteAttempts = 6;
    private readonly object _fileLock = new();
    private readonly string? _runtimeRoot;

    public SessionRegistry(string? dataDir = null)
    {
        _runtimeRoot = string.IsNullOrWhiteSpace(dataDir)
            ? null
            : Path.Combine(Path.GetFullPath(dataDir), "runtime", "projects");
    }

    internal string ChannelFilePath(string workspacePath, string fileName, bool migrateLegacy = true)
    {
        if (Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("A channel file name cannot contain a path.", nameof(fileName));
        var legacy = Path.Combine(workspacePath, ".agents", "channel", fileName);
        if (_runtimeRoot is null) return legacy;

        var normalized = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
        var leaf = Path.GetFileName(normalized).ToLowerInvariant();
        var directory = Path.Combine(_runtimeRoot, $"{leaf}-{hash}", "channel");
        var destination = Path.Combine(directory, fileName);
        if (migrateLegacy && !File.Exists(destination) && File.Exists(legacy))
        {
            Directory.CreateDirectory(directory);
            File.Copy(legacy, destination, overwrite: false);
        }
        return destination;
    }

    internal IReadOnlyList<string> ChannelFiles(string workspacePath, string pattern)
    {
        if (_runtimeRoot is null)
        {
            var legacyDirectory = Path.Combine(workspacePath, ".agents", "channel");
            return Directory.Exists(legacyDirectory)
                ? Directory.EnumerateFiles(legacyDirectory, pattern).ToList()
                : [];
        }

        var legacyDirectoryPath = Path.Combine(workspacePath, ".agents", "channel");
        if (Directory.Exists(legacyDirectoryPath))
        {
            foreach (var legacy in Directory.EnumerateFiles(legacyDirectoryPath, pattern))
                _ = ChannelFilePath(workspacePath, Path.GetFileName(legacy));
        }
        var runtimeDirectory = Path.GetDirectoryName(ChannelFilePath(workspacePath, ".probe", migrateLegacy: false))!;
        return Directory.Exists(runtimeDirectory)
            ? Directory.EnumerateFiles(runtimeDirectory, pattern).ToList()
            : [];
    }

    private string StatePath(string workspacePath) =>
        ChannelFilePath(workspacePath, "dispatch-state.json");

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

    private JsonObject LoadUnlocked(string workspacePath)
    {
        var path = StatePath(workspacePath);
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : (JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
    }

    private void SaveUnlocked(string workspacePath, JsonObject state)
    {
        var path = StatePath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAllTextWithRetry(
            path,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            File.WriteAllText,
            Thread.Sleep);
    }

    /// <summary>
    /// Windows can briefly reject writes while an agent process has the legacy state file
    /// open or memory-mapped. Treat that sharing violation like the transient condition it
    /// is instead of failing the whole agent run. Non-I/O failures still surface immediately.
    /// </summary>
    internal static void WriteAllTextWithRetry(
        string path,
        string content,
        Action<string, string> write,
        Action<TimeSpan> delay)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                write(path, content);
                return;
            }
            catch (IOException) when (attempt < WriteAttempts)
            {
                delay(TimeSpan.FromMilliseconds(25 * (1 << (attempt - 1))));
            }
        }
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

    public string? GetLastChatProvider(string workspacePath, string target)
    {
        var state = Load(workspacePath);
        return (state["_chatProviders"] as JsonObject)?[target]?.GetValue<string>();
    }

    public void SetLastChatProvider(string workspacePath, string target, string provider)
    {
        Update(workspacePath, state =>
        {
            var providers = state["_chatProviders"] as JsonObject ?? new JsonObject();
            providers[target] = provider;
            state["_chatProviders"] = providers;
        });
    }

    public void ClearLastChatProvider(string workspacePath, string target)
    {
        Update(workspacePath, state =>
        {
            if (state["_chatProviders"] is JsonObject providers)
                providers.Remove(target);
        });
    }

    public string? GetLastChatModel(string workspacePath, string target)
    {
        var state = Load(workspacePath);
        return (state["_chatModels"] as JsonObject)?[target]?.GetValue<string>();
    }

    public void SetLastChatModel(string workspacePath, string target, string model)
    {
        Update(workspacePath, state =>
        {
            var models = state["_chatModels"] as JsonObject ?? new JsonObject();
            models[target] = model;
            state["_chatModels"] = models;
        });
    }

    public void ClearLastChatModel(string workspacePath, string target)
    {
        Update(workspacePath, state =>
        {
            if (state["_chatModels"] is JsonObject models)
                models.Remove(target);
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

    /// <summary>Legacy shared snapshot (`_ticketSnapshot`) — one per workspace.</summary>
    public Dictionary<int, string> TicketSnapshot(string workspacePath)
    {
        var s = Load(workspacePath);
        return ParseSnapshot(s["_ticketSnapshot"] as JsonObject);
    }

    /// <summary>
    /// Per-automation ticket snapshot (`_ticketSnapshots[automationId]`). Snapshots are
    /// isolated per automation so one workflow committing its firing can never acknowledge
    /// a transition that ANOTHER workflow on the same trigger was still due to retry
    /// (backport analysis §2.4). An automation with no snapshot yet seeds from the legacy
    /// shared `_ticketSnapshot`, so upgrading (or adding an automation) never replays
    /// transitions that predate it.
    /// </summary>
    public Dictionary<int, string> TicketSnapshot(string workspacePath, string automationId)
    {
        var s = Load(workspacePath);
        var perAutomation = (s["_ticketSnapshots"] as JsonObject)?[automationId] as JsonObject;
        return ParseSnapshot(perAutomation ?? s["_ticketSnapshot"] as JsonObject);
    }

    private static Dictionary<int, string> ParseSnapshot(JsonObject? snap)
    {
        var dict = new Dictionary<int, string>();
        if (snap is null) return dict;
        foreach (var kv in snap)
            if (int.TryParse(kv.Key, out var id) && kv.Value is not null)
                dict[id] = kv.Value.GetValue<string>();
        return dict;
    }

    public void SaveTicketSnapshot(string workspacePath, IReadOnlyDictionary<int, string> snap)
    {
        Update(workspacePath, s => s["_ticketSnapshot"] = ToJson(snap));
    }

    public void SaveTicketSnapshot(string workspacePath, string automationId, IReadOnlyDictionary<int, string> snap)
    {
        Update(workspacePath, s =>
        {
            var all = s["_ticketSnapshots"] as JsonObject ?? new JsonObject();
            all[automationId] = ToJson(snap);
            s["_ticketSnapshots"] = all;
            // Write-through to the legacy shared snapshot: it stays a FRESH seed for
            // automations that don't have their own snapshot yet (new automations, or a
            // rollback to an older KittyClaw), instead of freezing at upgrade time and
            // replaying stale transitions.
            s["_ticketSnapshot"] = ToJson(snap);
        });
    }

    /// <summary>
    /// Atomically records a status transition for one automation. Returns false when that
    /// automation has already consumed the ticket in the target status.
    /// </summary>
    public bool TryConsumeStatusTransition(string workspacePath, string automationId, int ticketId, string status)
    {
        var consumed = false;
        Update(workspacePath, state =>
        {
            var all = state["_ticketSnapshots"] as JsonObject ?? new JsonObject();
            var snapshot = all[automationId] as JsonObject;
            if (snapshot is null)
            {
                snapshot = state["_ticketSnapshot"] is JsonObject legacy
                    ? (JsonObject)legacy.DeepClone()
                    : new JsonObject();
            }

            var key = ticketId.ToString();
            if (string.Equals(snapshot[key]?.GetValue<string>(), status, StringComparison.Ordinal))
                return;

            snapshot[key] = status;
            all[automationId] = snapshot;
            state["_ticketSnapshots"] = all;
            state["_ticketSnapshot"] = (JsonObject)snapshot.DeepClone();
            consumed = true;
        });
        return consumed;
    }

    private static JsonObject ToJson(IReadOnlyDictionary<int, string> snap)
    {
        var obj = new JsonObject();
        foreach (var kv in snap) obj[kv.Key.ToString()] = kv.Value;
        return obj;
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
