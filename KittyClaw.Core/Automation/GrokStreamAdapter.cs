using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Maps NDJSON lines from `grok --output-format streaming-json` onto an AgentRun,
/// normalizing them to the claude-style event kinds the rest of the pipeline consumes
/// ("assistant", "tool_use", "result", "error"). The grok stream is OpenCode-style and
/// its field names are not pinned by public docs, so every extraction here is tolerant:
/// multiple candidate field names, and a false return (→ generic passthrough in
/// AgentStreamPump) whenever a line doesn't look like something we understand.
/// </summary>
internal static class GrokStreamAdapter
{
    /// <summary>Returns true when the line was fully handled (events pushed / usage recorded).</summary>
    internal static bool TryMap(JsonElement root, string line, AgentRun run)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";

        switch (type)
        {
            case "text":
            case "message":
            case "assistant":
            {
                var text = ExtractText(root);
                // No extractable text → not a shape we know (e.g. a claude-style assistant
                // envelope from a mock) — let the generic handler deal with it.
                if (string.IsNullOrWhiteSpace(text)) return false;
                run.Push(new StreamEvent(DateTime.UtcNow, "assistant", $"[assistant] {text}"));
                return true;
            }
            case "tool_use":
            case "tool_call":
            case "tool":
            {
                var name = FirstString(root, "name", "tool", "toolName", "tool_name") ?? "tool";
                var input = FirstRawJson(root, "input", "args", "arguments", "parameters") ?? "{}";
                run.Push(new StreamEvent(DateTime.UtcNow, "tool_use", name, input));
                return true;
            }
            case "error":
            {
                var msg = FirstString(root, "message", "error", "text") ?? line;
                run.Push(new StreamEvent(DateTime.UtcNow, "error", msg, line));
                return true;
            }
        }

        // Terminal summary: the event (typed "result"/"step_finish"/"summary" or untyped) that
        // carries usage/cost/stopReason. Record usage and surface it as the "result" event the
        // runner's watchdog and cost tracking key on.
        if (type is "result" or "step_finish" or "summary" || (type == "" && LooksLikeSummary(root)))
        {
            var isFinal = type is "result" or "summary" || type == "" || HasAny(root, "stopReason", "stop_reason");
            RecordUsage(root, run);
            if (isFinal)
            {
                var text = ExtractText(root);
                run.Push(new StreamEvent(DateTime.UtcNow, "result",
                    string.IsNullOrWhiteSpace(text) ? "[result]" : $"[result] {text}", line));
            }
            return true;
        }

        return false;
    }

    private static bool LooksLikeSummary(JsonElement root) =>
        HasAny(root, "usage", "cost", "stopReason", "stop_reason");

    private static bool HasAny(JsonElement obj, params string[] names) =>
        names.Any(n => obj.TryGetProperty(n, out _));

    private static string? ExtractText(JsonElement root)
    {
        var direct = FirstString(root, "text", "content", "delta");
        if (direct is not null) return direct;
        // Nested shapes: {part: {text}}, {delta: {text}}, {message: {content: "..."}}
        foreach (var container in new[] { "part", "delta", "message" })
        {
            if (root.TryGetProperty(container, out var c) && c.ValueKind == JsonValueKind.Object)
            {
                var nested = FirstString(c, "text", "content");
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static string? FirstRawJson(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return v.GetRawText();
        return null;
    }

    // Token/cost accounting. Accepts snake_case and camelCase, Anthropic-style and
    // OpenAI-style field names. Accumulates on the run like the claude result path.
    private static void RecordUsage(JsonElement root, AgentRun run)
    {
        try
        {
            int input = 0, output = 0, cacheRead = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = FirstInt(usage, "input_tokens", "inputTokens", "prompt_tokens", "promptTokens");
                output = FirstInt(usage, "output_tokens", "outputTokens", "completion_tokens", "completionTokens");
                cacheRead = FirstInt(usage, "cache_read_input_tokens", "cachedTokens", "cached_tokens");
            }
            decimal? cost = ReadCost(root);
            if (input > 0 || output > 0 || cacheRead > 0 || cost is not null)
                run.AddUsage(input, output, cacheRead, 0, cost);
        }
        catch { /* usage telemetry must never break the stream pump */ }
    }

    private static decimal? ReadCost(JsonElement root)
    {
        if (!root.TryGetProperty("cost", out var c)) return null;
        if (c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var v)) return v;
        if (c.ValueKind == JsonValueKind.Object)
            foreach (var n in new[] { "total", "usd", "total_usd", "totalUsd" })
                if (c.TryGetProperty(n, out var nested) && nested.ValueKind == JsonValueKind.Number && nested.TryGetDecimal(out var nv))
                    return nv;
        return null;
    }

    private static int FirstInt(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        return 0;
    }
}
