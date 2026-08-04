using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Maps NDJSON lines from <c>grok --output-format streaming-json</c> onto an AgentRun,
/// normalizing them to the claude-style event kinds the rest of the pipeline consumes
/// ("assistant", "content_block_delta", "tool_use", "result", "error").
/// <para>
/// Observed real stream (grok 0.2.x): token chunks as <c>{"type":"text","data":"…"}</c>
/// and a terminal <c>{"type":"end","stopReason":"EndTurn","usage":…,"total_cost_usd":…}</c>.
/// Field names are not pinned by public docs, so extraction is tolerant (multiple candidate
/// names); unrecognized lines return false → generic passthrough in <see cref="AgentStreamPump"/>.
/// </para>
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
                // null = unknown shape (e.g. claude-style assistant envelope) → fall through.
                // Empty string = handled no-op. Non-empty (including whitespace tokens) streams.
                if (text is null) return false;
                if (text.Length == 0) return true;
                run.AppendStreamText(text);
                // Same live-streaming path the chat drawer uses for claude content_block_delta.
                run.Push(new StreamEvent(DateTime.UtcNow, "content_block_delta", text));
                return true;
            }
            case "tool_use":
            case "tool_call":
            case "tool":
            {
                // Flush any text that preceded this tool call so the chat shows it before the tool row.
                FlushAssistant(run);
                var name = FirstString(root, "name", "tool", "toolName", "tool_name") ?? "tool";
                var input = FirstRawJson(root, "input", "args", "arguments", "parameters") ?? "{}";
                run.Push(new StreamEvent(DateTime.UtcNow, "tool_use", name, input));
                return true;
            }
            case "error":
            {
                FlushAssistant(run);
                var msg = FirstString(root, "message", "error", "text", "data") ?? line;
                run.Push(new StreamEvent(DateTime.UtcNow, "error", msg, line));
                return true;
            }
        }

        // Terminal summary: "end" is what the real grok CLI emits; also accept result /
        // step_finish / summary and untyped usage payloads.
        if (type is "end" or "result" or "step_finish" or "summary" || (type == "" && LooksLikeSummary(root)))
        {
            var isFinal = type is "end" or "result" or "summary" || type == ""
                || HasAny(root, "stopReason", "stop_reason");
            RecordUsage(root, run);
            if (isFinal)
            {
                FlushAssistant(run);
                var text = ExtractText(root);
                run.Push(new StreamEvent(DateTime.UtcNow, "result",
                    string.IsNullOrWhiteSpace(text) ? "[result]" : $"[result] {text}", line));
            }
            return true;
        }

        return false;
    }

    private static void FlushAssistant(AgentRun run)
    {
        var accumulated = run.TakeStreamText();
        if (accumulated.Length == 0) return;
        run.Push(new StreamEvent(DateTime.UtcNow, "assistant", $"[assistant] {accumulated}"));
    }

    private static bool LooksLikeSummary(JsonElement root) =>
        HasAny(root, "usage", "cost", "total_cost_usd", "stopReason", "stop_reason");

    private static bool HasAny(JsonElement obj, params string[] names) =>
        names.Any(n => obj.TryGetProperty(n, out _));

    private static string? ExtractText(JsonElement root)
    {
        // Real grok streaming-json uses "data" for token chunks; also accept text/content/delta.
        var direct = FirstString(root, "data", "text", "content", "delta");
        if (direct is not null) return direct;
        // Nested shapes: {part: {text}}, {delta: {text}}, {message: {content: "..."}}
        foreach (var container in new[] { "part", "delta", "message", "data" })
        {
            if (root.TryGetProperty(container, out var c) && c.ValueKind == JsonValueKind.Object)
            {
                var nested = FirstString(c, "text", "content", "data");
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
                cacheRead = FirstInt(usage, "cache_read_input_tokens", "cacheReadInputTokens", "cachedTokens", "cached_tokens");
            }
            decimal? cost = ReadCost(root);
            if (input > 0 || output > 0 || cacheRead > 0 || cost is not null)
            {
                // OpenAI-compatible usage counts cached tokens inside prompt/input tokens.
                var uncachedInput = Math.Max(0, input - cacheRead);
                decimal estimatedCost = 0m;
                var estimated = cost is null && ModelCostEstimator.TryEstimate(
                    run.Model, uncachedInput, output, cacheRead, 0, out estimatedCost);
                run.AddUsage(uncachedInput, output, cacheRead, 0,
                    cost ?? (estimated ? estimatedCost : null), estimated);
            }
        }
        catch { /* usage telemetry must never break the stream pump */ }
    }

    private static decimal? ReadCost(JsonElement root)
    {
        // Real grok "end" events put the total at the top level as total_cost_usd.
        foreach (var n in new[] { "total_cost_usd", "totalCostUsd", "cost" })
        {
            if (!root.TryGetProperty(n, out var c)) continue;
            if (c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var v)) return v;
            if (c.ValueKind == JsonValueKind.Object)
            {
                foreach (var nestedName in new[] { "total", "usd", "total_usd", "totalUsd" })
                    if (c.TryGetProperty(nestedName, out var nested) && nested.ValueKind == JsonValueKind.Number
                        && nested.TryGetDecimal(out var nv))
                        return nv;
            }
        }
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
