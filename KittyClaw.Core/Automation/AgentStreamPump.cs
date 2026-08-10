using System.Diagnostics;
using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>Pumps stdout, stderr, and steering stdin between a claude subprocess and an AgentRun.</summary>
internal static class AgentStreamPump
{
    internal static Task PumpStdoutAsync(Process proc, AgentRun run, CancellationToken ct) =>
        PumpStdoutAsync(proc, run, AgentCliBackend.For(CliProvider.Claude), ct);

    internal static async Task PumpStdoutAsync(Process proc, AgentRun run, CliProvider provider, CancellationToken ct)
        => await PumpStdoutAsync(proc, run, AgentCliBackend.For(provider), ct);

    internal static async Task PumpStdoutAsync(
        Process proc, AgentRun run, AgentCliBackend backend, CancellationToken ct)
    {
        var reader = proc.StandardOutput;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    run.Push(new StreamEvent(DateTime.UtcNow, "error", $"stdout read error: {ex.Message}"));
                    break;
                }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { await run.WaitForApprovalResolutionAsync(ct); }
                catch (OperationCanceledException) { break; }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    // Native backends normalize their JSONL dialect. Claude returns false and
                    // uses the established generic stream-json handling below.
                    if (backend.TryMapEvent(doc.RootElement, line, run))
                        continue;
                    var kind = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "event" : "event";
                    // For assistant message events: emit the assistant text first, then separate tool_use events
                    if (kind == "assistant" &&
                        doc.RootElement.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.Array)
                    {
                        var hasText = false;
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text")
                            {
                                hasText = true;
                                break;
                            }
                        }
                        if (hasText)
                        {
                            run.Push(new StreamEvent(DateTime.UtcNow, kind, AgentRunner.FlattenJson(doc.RootElement)));
                        }
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var ptype) && ptype.GetString() == "tool_use")
                            {
                                var toolName = part.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                                var toolInput = part.TryGetProperty("input", out var inp) ? inp.ToString() : "{}";
                                var eventKind = toolName == "AskUserQuestion" ? "ask_user_question" : "tool_use";
                                if (eventKind == "ask_user_question")
                                    run.IsAwaitingUserAnswer = true;
                                var toolId = part.TryGetProperty("id", out var id) ? id.GetString() : null;
                                run.Push(new StreamEvent(DateTime.UtcNow, eventKind, toolName, toolInput, toolId));
                            }
                        }
                    }
                    else if (kind == "user" &&
                             doc.RootElement.TryGetProperty("message", out var userMessage) &&
                             userMessage.TryGetProperty("content", out var userContent) &&
                             userContent.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in userContent.EnumerateArray())
                        {
                            if (!part.TryGetProperty("type", out var partType) || partType.GetString() != "tool_result") continue;
                            var toolId = part.TryGetProperty("tool_use_id", out var id) ? id.GetString() : null;
                            var failed = part.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                            run.Push(new StreamEvent(DateTime.UtcNow, "tool_result", failed ? "failed" : "succeeded", part.GetRawText(), toolId));
                        }
                    }
                    else
                    {
                        // The terminal result event carries the whole turn's token usage and
                        // priced cost — record it before the max_turns remap below, because an
                        // error_max_turns result still reports the tokens it burned.
                        if (kind == "result")
                            TryRecordUsage(doc.RootElement, run);
                        if (kind == "result" &&
                            doc.RootElement.TryGetProperty("subtype", out var subtype) &&
                            subtype.GetString() == "error_max_turns")
                        {
                            kind = "max_turns";
                        }
                        // Carry the raw JSON as Detail for result / rate_limit_event events so
                        // the quota detector can inspect their fields (status, result text)
                        // regardless of how FlattenJson collapses the event for display.
                        var detail = kind is "result" or "rate_limit_event" ? line : null;
                        run.Push(new StreamEvent(DateTime.UtcNow, kind, AgentRunner.FlattenJson(doc.RootElement), detail));
                    }
                }
                catch
                {
                    run.Push(new StreamEvent(DateTime.UtcNow, "raw", line));
                }
            }
        }
        catch (Exception ex)
        {
            try { run.Push(new StreamEvent(DateTime.UtcNow, "error", $"stdout pump failed: {ex.Message}")); } catch { /* subscriber may throw */ }
        }
    }

    // Extracts `usage` (input/output/cache token counts) and `total_cost_usd` from a result
    // event and accumulates them on the run. Accumulate — not assign — because one AgentRun can
    // spawn several subprocesses (resume retry, quota fallback, steer replay), each with its own
    // result event.
    private static void TryRecordUsage(JsonElement root, AgentRun run)
    {
        try
        {
            int input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = ReadInt(usage, "input_tokens");
                output = ReadInt(usage, "output_tokens");
                cacheRead = ReadInt(usage, "cache_read_input_tokens");
                cacheWrite = ReadInt(usage, "cache_creation_input_tokens");
            }
            decimal? cost = null;
            if (root.TryGetProperty("total_cost_usd", out var c) &&
                c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var cv))
            {
                cost = cv;
            }
            if (input > 0 || output > 0 || cacheRead > 0 || cacheWrite > 0 || cost is not null)
                run.AddUsage(input, output, cacheRead, cacheWrite, cost);
        }
        catch { /* usage telemetry must never break the stream pump */ }
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    internal static async Task PumpStderrAsync(Process proc, AgentRun run, CancellationToken ct)
        => await PumpStderrAsync(proc, run, AgentCliBackend.For(CliProvider.Claude), ct);

    internal static async Task PumpStderrAsync(
        Process proc, AgentRun run, AgentCliBackend backend, CancellationToken ct)
    {
        var reader = proc.StandardError;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    try { run.Push(new StreamEvent(DateTime.UtcNow, "error", $"stderr read error: {ex.Message}")); } catch { /* subscriber may throw */ }
                    break;
                }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                run.Push(new StreamEvent(DateTime.UtcNow, backend.MapStderrKind(line), line));
            }
        }
        catch (Exception ex)
        {
            try { run.Push(new StreamEvent(DateTime.UtcNow, "error", $"stderr pump failed: {ex.Message}")); } catch { /* subscriber may throw */ }
        }
    }

    internal static async Task PumpSteeringAsync(Process proc, AgentRun run, CancellationToken ct)
    {
        // Best-effort steering: write queued messages to stdin while it's still open.
        // With --print mode, claude closes its own stdin read after the initial prompt
        // is consumed, so messages arriving after that will be held in the queue and
        // replayed on the next --resume invocation (handled by the engine).
        try
        {
            while (await run.SteeringQueue.Reader.WaitToReadAsync(ct))
            {
                while (run.SteeringQueue.Reader.TryRead(out var msg))
                {
                    run.Push(new StreamEvent(DateTime.UtcNow, "steer", msg));
                    try
                    {
                        if (proc.StandardInput.BaseStream.CanWrite)
                        {
                            await proc.StandardInput.WriteLineAsync(msg);
                            await proc.StandardInput.FlushAsync(ct);
                        }
                        else
                        {
                            run.AddPendingSteerMessage(msg);
                        }
                    }
                    catch { run.AddPendingSteerMessage(msg); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
