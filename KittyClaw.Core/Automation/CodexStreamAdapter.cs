using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>Maps <c>codex exec --json</c> JSONL events to KittyClaw stream events.</summary>
internal static class CodexStreamAdapter
{
    internal static bool TryMap(JsonElement root, string line, AgentRun run)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var type = String(root, "type");

        switch (type)
        {
            case "thread.started":
                var threadId = String(root, "thread_id");
                if (!string.IsNullOrWhiteSpace(threadId)) run.SessionId = threadId;
                run.Push(new StreamEvent(DateTime.UtcNow, "system",
                    string.IsNullOrWhiteSpace(threadId) ? "Codex session started" : $"Codex session {threadId[..Math.Min(8, threadId.Length)]} started",
                    line));
                return true;

            case "turn.started":
                return true;

            case "item.started":
            case "item.updated":
            case "item.completed":
                return MapItem(root, line, run, type == "item.completed");

            case "turn.completed":
                RecordUsage(root, run);
                run.Push(new StreamEvent(DateTime.UtcNow, "result", "[result]", line));
                return true;

            case "turn.failed":
            case "error":
                run.Push(new StreamEvent(DateTime.UtcNow, "error",
                    String(root, "message") ?? ExtractError(root) ?? line, line));
                return true;
        }

        return false;
    }

    private static bool MapItem(JsonElement root, string line, AgentRun run, bool completed)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return false;

        var itemType = String(item, "type") ?? "item";
        if (itemType == "agent_message")
        {
            if (!completed) return true;
            var text = String(item, "text");
            if (!string.IsNullOrEmpty(text))
                run.Push(new StreamEvent(DateTime.UtcNow, "assistant", $"[assistant] {text}"));
            return true;
        }

        if (itemType == "reasoning")
        {
            // Codex may split its internal reasoning into tiny completed items around tool
            // calls (for example a lone "{" or one JSON property). Those fragments are not
            // user-facing output and render as disconnected Markdown blocks in the drawer.
            return true;
        }

        var correlationId = String(item, "id");
        if (!completed)
        {
            var name = itemType switch
            {
                "command_execution" => "command",
                "file_change" => "file_change",
                "mcp_tool_call" => String(item, "tool") ?? String(item, "name") ?? "mcp",
                "web_search" => "web_search",
                "plan" => "plan",
                _ => itemType,
            };
            var detail = item.TryGetProperty("command", out var command)
                ? command.ToString()
                : line;
            run.Push(new StreamEvent(DateTime.UtcNow, "tool_use", name, detail, correlationId));
        }
        else
        {
            var outcome = String(item, "status") switch
            {
                "failed" => "failed",
                "cancelled" => "cancelled",
                "canceled" => "cancelled",
                _ => "succeeded",
            };
            run.Push(new StreamEvent(DateTime.UtcNow, "tool_result", outcome, line, correlationId));
        }
        return true;
    }

    private static void RecordUsage(JsonElement root, AgentRun run)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;
        var input = Int(usage, "input_tokens");
        var cached = Int(usage, "cached_input_tokens");
        var output = Int(usage, "output_tokens");
        if (input > 0 || cached > 0 || output > 0)
        {
            // OpenAI reports cached input as a subset of input_tokens. AgentRun stores token
            // classes separately, so keep only the uncached remainder in InputTokens.
            var uncachedInput = Math.Max(0, input - cached);
            var estimated = ModelCostEstimator.TryEstimate(
                run.Model, uncachedInput, output, cached, 0, out var cost);
            run.AddUsage(uncachedInput, output, cached, 0, estimated ? cost : null, estimated);
        }
    }

    private static string? ExtractError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return null;
        return error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : error.TryGetProperty("message", out var message) ? message.GetString() : error.ToString();
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result) ? result : 0;
}
