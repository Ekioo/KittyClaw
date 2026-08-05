using System.Text;
using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>Maps NDJSON history entries emitted by <c>vibe --output streaming</c>.</summary>
internal static class MistralStreamAdapter
{
    internal static bool TryMap(JsonElement root, string line, AgentRun run)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        var sessionId = String(root, "sessionId") ?? String(root, "session_id");
        if (!string.IsNullOrWhiteSpace(sessionId)) run.SessionId = sessionId;

        // Vibe replays completed history when a session is resumed. Ignore entries created
        // before this KittyClaw run so old assistant messages and tools are not duplicated.
        var createdAt = Long(root, "createdAt", "created_at");
        if (createdAt > 0 && createdAt < new DateTimeOffset(run.StartedAt.ToUniversalTime()).ToUnixTimeMilliseconds())
            return true;

        switch (String(root, "type"))
        {
            case "message":
                if (!string.Equals(String(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                    return true;
                var text = ContentText(root);
                if (!string.IsNullOrWhiteSpace(text))
                    run.Push(new StreamEvent(DateTime.UtcNow, "assistant", $"[assistant] {text}"));
                return true;

            case "effect":
                var title = String(root, "title") ?? "tool";
                var detail = root.TryGetProperty("detail", out var effectDetail)
                    ? effectDetail.GetRawText() : line;
                run.Push(new StreamEvent(DateTime.UtcNow, "tool_use", title, detail));
                return true;

            case "notice":
                var level = String(root, "level");
                var message = String(root, "message") ?? line;
                run.Push(new StreamEvent(DateTime.UtcNow,
                    string.Equals(level, "error", StringComparison.OrdinalIgnoreCase) ? "error" : "diagnostic",
                    message, line));
                return true;

            case "reasoning":
            case "callback":
            case "checkpoint":
                return true;
        }
        return false;
    }

    private static string ContentText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        var result = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object &&
                string.Equals(String(block, "type"), "text", StringComparison.OrdinalIgnoreCase) &&
                String(block, "text") is { } text)
            {
                if (result.Length > 0) result.AppendLine().AppendLine();
                result.Append(text);
            }
        return result.ToString();
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long Long(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var result)) return result;
        return 0;
    }
}
