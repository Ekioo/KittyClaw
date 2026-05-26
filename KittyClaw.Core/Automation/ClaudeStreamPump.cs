using System.Diagnostics;
using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>Pumps stdout, stderr, and steering stdin between a claude subprocess and an AgentRun.</summary>
internal static class ClaudeStreamPump
{
    internal static async Task PumpStdoutAsync(Process proc, AgentRun run, CancellationToken ct)
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
                try
                {
                    using var doc = JsonDocument.Parse(line);
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
                            run.Push(new StreamEvent(DateTime.UtcNow, kind, ClaudeRunner.FlattenJson(doc.RootElement)));
                        }
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var ptype) && ptype.GetString() == "tool_use")
                            {
                                var toolName = part.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                                var toolInput = part.TryGetProperty("input", out var inp) ? inp.ToString() : "{}";
                                var eventKind = toolName == "AskUserQuestion" ? "ask_user_question" : "tool_use";
                                run.Push(new StreamEvent(DateTime.UtcNow, eventKind, toolName, toolInput));
                            }
                        }
                    }
                    else
                    {
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
                        run.Push(new StreamEvent(DateTime.UtcNow, kind, ClaudeRunner.FlattenJson(doc.RootElement), detail));
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

    internal static async Task PumpStderrAsync(Process proc, AgentRun run, CancellationToken ct)
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
                run.Push(new StreamEvent(DateTime.UtcNow, "stderr", line));
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
