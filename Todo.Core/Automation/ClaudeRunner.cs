using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Todo.Core.Automation;

public sealed class ClaudeRunContext
{
    public required string ProjectSlug { get; init; }
    public required string WorkspacePath { get; init; }
    public required string AgentName { get; init; }
    public required string SkillFile { get; init; }
    public int? TicketId { get; init; }
    public string? TicketTitle { get; init; }
    public string? TicketStatus { get; init; }
    public int MaxTurns { get; init; } = 200;
    public string ConcurrencyGroup { get; init; } = "";
    public IDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public string? Model { get; init; }
    public string? ExtraContext { get; init; }
    public string? InlineSkillContent { get; init; }
    public string? PresetRunId { get; init; }
}

public sealed class ClaudeRunner
{
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<ClaudeRunner> _logger;

    public ClaudeRunner(SessionRegistry sessions, AgentRunRegistry runs, ILogger<ClaudeRunner> logger)
    {
        _sessions = sessions;
        _runs = runs;
        _logger = logger;
    }

    public async Task<AgentRun> RunAsync(ClaudeRunContext ctx, CancellationToken ct)
    {
        var run = new AgentRun
        {
            RunId = ctx.PresetRunId ?? Guid.NewGuid().ToString("N"),
            ProjectSlug = ctx.ProjectSlug,
            TicketId = ctx.TicketId,
            AgentName = ctx.AgentName,
            SkillFile = ctx.SkillFile,
            ConcurrencyGroup = string.IsNullOrEmpty(ctx.ConcurrencyGroup) ? ctx.AgentName : ctx.ConcurrencyGroup,
            StartedAt = DateTime.UtcNow,
        };
        _runs.Register(run);

        string skillContent;
        if (ctx.InlineSkillContent is not null)
        {
            skillContent = ctx.InlineSkillContent;
        }
        else
        {
            var skillAbs = Path.IsPathRooted(ctx.SkillFile)
                ? ctx.SkillFile
                : Path.Combine(ctx.WorkspacePath, ".agents", ctx.SkillFile);

            if (!File.Exists(skillAbs))
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "error", $"Skill file not found: {skillAbs}"));
                _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
                return run;
            }
            skillContent = await File.ReadAllTextAsync(skillAbs, ct);
        }

        // Session key matches the legacy dispatcher.mjs format ({agent}:{ticketId|sweep}).
        // We persist sessions for ALL runs — even those without a ticket (groomer,
        // documentalist, code-janitor, evaluator) — so they keep their context across restarts.
        var existingSessionId = _sessions.GetSessionId(ctx.WorkspacePath, ctx.AgentName, ctx.TicketId);
        var sessionId = existingSessionId ?? Guid.NewGuid().ToString();
        var isResume = existingSessionId is not null;
        run.SessionId = sessionId;
        _sessions.SetSessionId(ctx.WorkspacePath, ctx.AgentName, ctx.TicketId, sessionId);
        var prompt = BuildPrompt(ctx, skillContent, isResume);
        var sessionName = ctx.TicketId is not null ? $"{ctx.AgentName} #{ctx.TicketId}" : ctx.AgentName;

        var args = new List<string>
        {
            "--print", "--verbose",
            "--output-format", "stream-json",
            "--dangerously-skip-permissions",
            "--max-turns", ctx.MaxTurns.ToString(),
            "--remote-control",
        };
        if (isResume) { args.Add("--resume"); args.Add(sessionId); }
        else { args.Add("-n"); args.Add(sessionName); args.Add("--session-id"); args.Add(sessionId); }
        if (ctx.Model is not null) { args.Add("--model"); args.Add(ctx.Model); }

        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            WorkingDirectory = ctx.WorkspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_AGENT"] = ctx.AgentName;
        foreach (var kv in ctx.Env) psi.Environment[kv.Key] = kv.Value;

        AppendDebugLog(ctx, $"LAUNCHING {ctx.AgentName} {(isResume ? "(resume)" : "(new)")} ticket=#{ctx.TicketId} session={sessionId}");
        _logger.LogInformation("LAUNCH {Agent} {Mode} ticket=#{TicketId} session={SessionId} cmd=claude {Args}",
            ctx.AgentName, isResume ? "(resume)" : "(new)", ctx.TicketId, sessionId, string.Join(" ", args));

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "error", $"spawn failed: {ex.Message}"));
            _runs.Complete(run.RunId, AgentRunStatus.Failed, -1);
            return run;
        }

        run.Push(new StreamEvent(DateTime.UtcNow, "launch",
            $"{ctx.AgentName} {(isResume ? "(resume)" : "(new)")} session={sessionId[..8]} cwd={ctx.WorkspacePath} skill={ctx.SkillFile}"));

        try
        {
            await proc.StandardInput.WriteAsync(prompt);
            await proc.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "error", $"stdin write failed: {ex.Message}"));
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Cancellation.Token);
        var stdoutTask = PumpStdoutAsync(proc, run, linked.Token);
        var stderrTask = PumpStderrAsync(proc, run, linked.Token);
        var steerTask = PumpSteeringAsync(proc, run, linked.Token);

        // When cancelled externally, kill the process.
        using var killReg = linked.Token.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        int? exit = null;
        try
        {
            // Close stdin so claude knows the prompt is complete.
            try { proc.StandardInput.Close(); } catch { }
            await proc.WaitForExitAsync(linked.Token);
            exit = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            AppendDebugLog(ctx, $"STOPPED {ctx.AgentName} run={run.RunId}");
            return run;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        try { steerTask.Dispose(); } catch { }

        _runs.Complete(run.RunId, exit == 0 ? AgentRunStatus.Completed : AgentRunStatus.Failed, exit);
        AppendDebugLog(ctx, $"FINISHED {ctx.AgentName} run={run.RunId} exit={exit}");
        return run;
    }

    private static string BuildPrompt(ClaudeRunContext ctx, string skillContent, bool isResume)
    {
        if (isResume && ctx.TicketId is not null)
            return $"The owner has posted feedback on ticket #{ctx.TicketId}: {ctx.TicketTitle}\nRead ALL owner comments on this ticket and address them.";
        if (ctx.TicketId is not null)
            return $"{skillContent}\n\nFocus on ticket #{ctx.TicketId}: {ctx.TicketTitle}";
        return ctx.ExtraContext is null ? skillContent : $"{skillContent}\n\n{ctx.ExtraContext}";
    }

    private static async Task PumpStdoutAsync(Process proc, AgentRun run, CancellationToken ct)
    {
        var reader = proc.StandardOutput;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var kind = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "event" : "event";
                run.Push(new StreamEvent(DateTime.UtcNow, kind, FlattenJson(doc.RootElement)));
            }
            catch
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "raw", line));
            }
        }
    }

    private static async Task PumpStderrAsync(Process proc, AgentRun run, CancellationToken ct)
    {
        var reader = proc.StandardError;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            run.Push(new StreamEvent(DateTime.UtcNow, "stderr", line));
        }
    }

    private static async Task PumpSteeringAsync(Process proc, AgentRun run, CancellationToken ct)
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
                    }
                    catch { /* stdin already closed */ }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string FlattenJson(JsonElement e)
    {
        // Produce a short human-readable line per event.
        if (e.ValueKind != JsonValueKind.Object) return e.ToString();
        var sb = new StringBuilder();
        if (e.TryGetProperty("type", out var t)) sb.Append('[').Append(t.GetString()).Append("] ");
        if (e.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("text", out var dtext)) sb.Append(dtext.GetString());
        }
        if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            if (m.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text)) sb.Append(text.GetString());
                        else if (part.TryGetProperty("name", out var name)) sb.Append("tool:").Append(name.GetString()).Append(' ');
                    }
                }
                else if (content.ValueKind == JsonValueKind.String) sb.Append(content.GetString());
            }
        }
        if (sb.Length == 0) sb.Append(e.ToString());
        return sb.ToString();
    }

    private static void AppendDebugLog(ClaudeRunContext ctx, string line)
    {
        try
        {
            var dir = Path.Combine(ctx.WorkspacePath, ".agents", "channel");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"),
                $"[{DateTime.UtcNow:o}] {line}\n");
        }
        catch { }
    }
}
