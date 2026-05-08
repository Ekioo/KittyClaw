using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

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

    /// <summary>Project-wide fallback model. If a run hits a quota / usage-limit error from the claude CLI,
    /// the runner retries once with this model in the same AgentRun. Null disables the fallback.</summary>
    public string? FallbackModel { get; init; }

    public string? ExtraContext { get; init; }
    public string? InlineSkillContent { get; init; }
    public string? PresetRunId { get; init; }

    /// <summary>Optional namespace prefix for the SessionRegistry key (e.g. "chat" → "chat:agent:sweep"). Keeps chat sessions isolated from automation sessions for the same agent.</summary>
    public string? SessionScope { get; init; }

    /// <summary>If true and the run was a --resume that produced no assistant output and exited non-zero, the runner will silently invalidate the session and respawn with a fresh one in the same AgentRun.</summary>
    public bool RetryOnResumeFailure { get; init; }

    /// <summary>Callback invoked for every StreamEvent pushed onto the AgentRun. Wired before any event is emitted, so no race with subscribers attaching after the fact.</summary>
    public Action<StreamEvent>? OnEventHook { get; init; }
}

public sealed class ClaudeRunner
{
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly RunConcurrencyGate _gate;
    private readonly ILogger<ClaudeRunner> _logger;

    // Resolved once per process. Search order:
    //   0. KITTYCLAW_CLAUDE_BIN env var (escape hatch)
    //   1. Sibling of host exe: <baseDir>/claude(.exe)
    //   2. <baseDir>/tools/claude(.exe)
    //   3. Walk up to a repo root and look for KittyClaw.ClaudeMock/bin/<config>/net10.0/claude(.exe) (dev)
    //   4. "claude" — resolved via PATH (production default)
    private static readonly Lazy<string> _claudeBinary = new(ResolveClaudeBinary);

    private static string ResolveApiUrl()
    {
        // Pick the first http(s) URL the host process is listening on, falling back to the
        // historical default (5230). Stripped of trailing slashes so skills can append paths.
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls))
        {
            var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(first))
                return first.TrimEnd('/');
        }
        return "http://localhost:5230";
    }

    private static string ResolveClaudeBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var baseDir = AppContext.BaseDirectory;

        var sibling = Path.Combine(baseDir, exe);
        if (File.Exists(sibling)) return sibling;

        var tools = Path.Combine(baseDir, "tools", exe);
        if (File.Exists(tools)) return tools;

        // Walk up looking for KittyClaw.ClaudeMock/bin/**/claude(.exe) (dev mode)
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var mockProj = Path.Combine(dir.FullName, "KittyClaw.ClaudeMock", "bin");
            if (Directory.Exists(mockProj))
            {
                var found = Directory.EnumerateFiles(mockProj, exe, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found;
            }
        }

        return "claude";
    }

    public ClaudeRunner(SessionRegistry sessions, AgentRunRegistry runs, RunConcurrencyGate gate, ILogger<ClaudeRunner> logger)
    {
        _sessions = sessions;
        _runs = runs;
        _gate = gate;
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
        if (ctx.OnEventHook is not null) run.OnEvent += ctx.OnEventHook;
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
        // SessionScope optionally namespaces the key (e.g. "chat:agent:sweep") so chat
        // sessions don't collide with automation sessions for the same agent.
        var scopedAgent = ctx.SessionScope is null ? ctx.AgentName : $"{ctx.SessionScope}:{ctx.AgentName}";
        var existingSessionId = _sessions.GetSessionId(ctx.WorkspacePath, scopedAgent, ctx.TicketId);
        var sessionId = existingSessionId ?? Guid.NewGuid().ToString();
        var isResume = existingSessionId is not null;
        run.SessionId = sessionId;
        _sessions.SetSessionId(ctx.WorkspacePath, scopedAgent, ctx.TicketId, sessionId);

        // Global concurrency gate: cap simultaneous claude subprocesses across all projects
        // so the host doesn't OOM under heavy automation. Chats bypass entirely.
        var isChat = ctx.SessionScope == "chat";
        IDisposable slot;
        var snap = _gate.Snapshot();
        if (!isChat && snap.Active >= snap.Max)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "queued",
                $"Waiting for a free agent slot ({snap.Active}/{snap.Max} active, {snap.Queued} queued ahead)"));
        }
        try
        {
            slot = await _gate.AcquireAsync(isChat, ctx.AgentName, run.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            return run;
        }

        try
        {
            var attempt = await SpawnAndWaitAsync(ctx, run, skillContent, sessionId, isResume, modelOverride: null, ct);
            if (attempt.Cancelled) return run;

            if (ctx.RetryOnResumeFailure && isResume && (attempt.Exit ?? -1) != 0 && attempt.AssistantEventCount == 0)
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "reset",
                    "Previous session expired, starting a new one"));
                _sessions.Clear(ctx.WorkspacePath, scopedAgent, ctx.TicketId);
                sessionId = Guid.NewGuid().ToString();
                run.SessionId = sessionId;
                _sessions.SetSessionId(ctx.WorkspacePath, scopedAgent, ctx.TicketId, sessionId);

                attempt = await SpawnAndWaitAsync(ctx, run, skillContent, sessionId, isResume: false, modelOverride: null, ct);
                if (attempt.Cancelled) return run;
            }

            // Quota / usage-limit fallback: if the CLI signalled a rate-limit or weekly quota
            // error and the project has a FallbackModel configured (and it differs from the
            // primary model that just failed), retry the run once with the fallback model.
            if (attempt.HitQuota
                && !string.IsNullOrWhiteSpace(ctx.FallbackModel)
                && !string.Equals(ctx.FallbackModel, ctx.Model, StringComparison.OrdinalIgnoreCase))
            {
                run.Push(new StreamEvent(DateTime.UtcNow, "fallback",
                    $"Quota reached on {(ctx.Model ?? "default model")} — retrying with fallback model {ctx.FallbackModel}"));
                _logger.LogWarning("Quota hit for {Agent} (model={Model}); falling back to {Fallback}",
                    ctx.AgentName, ctx.Model, ctx.FallbackModel);
                attempt = await SpawnAndWaitAsync(ctx, run, skillContent, sessionId, isResume: false, modelOverride: ctx.FallbackModel, ct);
                if (attempt.Cancelled) return run;
            }

            _runs.Complete(run.RunId, attempt.Exit == 0 ? AgentRunStatus.Completed : AgentRunStatus.Failed, attempt.Exit);
            AppendDebugLog(ctx, $"FINISHED {ctx.AgentName} run={run.RunId} exit={attempt.Exit}");
            return run;
        }
        finally
        {
            slot.Dispose();
        }
    }

    private readonly record struct SpawnResult(int? Exit, int AssistantEventCount, bool Cancelled, bool HitQuota);

    // Heuristic patterns matching quota / usage-limit / rate-limit messages emitted by the
    // claude CLI (via stream-json result events or stderr). Kept broad on purpose — false
    // positives only cause one extra retry on the fallback model, which is recoverable.
    private static readonly string[] QuotaMarkers =
    {
        "usage limit reached",
        "claude ai usage limit",
        "rate_limit_error",
        "rate limit",
        "quota exceeded",
        "weekly limit",
        "5-hour limit",
    };

    private static bool LooksLikeQuotaError(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var marker in QuotaMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private async Task<SpawnResult> SpawnAndWaitAsync(
        ClaudeRunContext ctx, AgentRun run, string skillContent,
        string sessionId, bool isResume, string? modelOverride, CancellationToken ct)
    {
        var prompt = await BuildPromptAsync(ctx, skillContent, isResume, ct);
        var sessionName = ctx.TicketId is not null ? $"{ctx.AgentName} #{ctx.TicketId}" : ctx.AgentName;

        var args = new List<string>
        {
            "--print", "--verbose",
            "--output-format", "stream-json",
            "--dangerously-skip-permissions",
            "--max-turns", ctx.MaxTurns.ToString(),
            "--remote-control",
            // KittyClaw owns the agent memory layer (.agents/{agent}/memory.md committed to
            // the workspace repo). Disable claude's built-in Memory tool so agents don't
            // also write to their per-host memory store and end up with two divergent
            // sources of truth.
            "--disallowed-tools", "Memory",
        };
        if (isResume) { args.Add("--resume"); args.Add(sessionId); }
        else { args.Add("-n"); args.Add(sessionName); args.Add("--session-id"); args.Add(sessionId); }
        var effectiveModel = modelOverride ?? ctx.Model;
        if (effectiveModel is not null) { args.Add("--model"); args.Add(effectiveModel); }

        var psi = new ProcessStartInfo
        {
            FileName = _claudeBinary.Value,
            WorkingDirectory = ctx.WorkspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_AGENT"] = ctx.AgentName;
        // Tell skills which API URL to talk to. Skills resolve `${KITTYCLAW_API_URL:-http://localhost:5230}`
        // so they hit the *current* host instance even when running on a non-default port (e.g. an
        // isolated test instance spawned by KittyClaw.QaRunner).
        psi.Environment["KITTYCLAW_API_URL"] = ResolveApiUrl();
        foreach (var kv in ctx.Env) psi.Environment[kv.Key] = kv.Value;

        AppendDebugLog(ctx, $"LAUNCHING {ctx.AgentName} {(isResume ? "(resume)" : "(new)")} ticket=#{ctx.TicketId} session={sessionId}");
        _logger.LogInformation("LAUNCH {Agent} {Mode} ticket=#{TicketId} session={SessionId} cmd={Bin} {Args}",
            ctx.AgentName, isResume ? "(resume)" : "(new)", ctx.TicketId, sessionId, _claudeBinary.Value, string.Join(" ", args));

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            run.Push(new StreamEvent(DateTime.UtcNow, "error", $"spawn failed: {ex.Message}"));
            return new SpawnResult(-1, 0, false, false);
        }

        run.Push(new StreamEvent(DateTime.UtcNow, "launch",
            $"{ctx.AgentName} {(isResume ? "(resume)" : "(new)")} session={sessionId[..8]} cwd={ctx.WorkspacePath} skill={ctx.SkillFile}"));

        // Count assistant events emitted during THIS attempt only, and watch for quota
        // markers in stream-json events / stderr so the outer RunAsync can decide whether
        // to retry with a fallback model.
        var assistantCount = 0;
        var hitQuota = 0;
        Action<StreamEvent> counter = ev =>
        {
            if (ev.Kind == "assistant") Interlocked.Increment(ref assistantCount);
            if (hitQuota == 0 && (ev.Kind == "stderr" || ev.Kind == "result" || ev.Kind == "raw" || ev.Kind == "error"))
            {
                if (LooksLikeQuotaError(ev.Detail) || LooksLikeQuotaError(ev.Text))
                    Interlocked.Exchange(ref hitQuota, 1);
            }
        };
        run.OnEvent += counter;

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

        using var killReg = linked.Token.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* cleanup, process may already be exiting */ }
        });

        int? exit;
        try
        {
            try { proc.StandardInput.Close(); } catch { /* stdin may already be closed */ }
            await proc.WaitForExitAsync(linked.Token);
            exit = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* cleanup on cancellation */ }
            _runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            AppendDebugLog(ctx, $"STOPPED {ctx.AgentName} run={run.RunId}");
            run.OnEvent -= counter;
            return new SpawnResult(null, assistantCount, true, hitQuota == 1);
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        try { steerTask.Dispose(); } catch { /* best-effort cleanup */ }
        run.OnEvent -= counter;
        return new SpawnResult(exit, assistantCount, false, hitQuota == 1);
    }

    private static async Task<string> BuildPromptAsync(ClaudeRunContext ctx, string skillContent, bool isResume, CancellationToken ct)
    {
        // Chat resume: each turn just sends the user's message. The skill/preamble was
        // injected when the session was created and is preserved across resumes by claude.
        if (ctx.SessionScope == "chat" && isResume)
            return ctx.ExtraContext ?? "";

        // Automation resume on a ticket: ping the agent that the owner posted new feedback.
        if (isResume && ctx.TicketId is not null)
            return $"The owner has posted feedback on ticket #{ctx.TicketId}: {ctx.TicketTitle}\nRead ALL owner comments on this ticket and address them.";

        var prefix = await BuildPreambleAsync(ctx, ct);

        if (ctx.TicketId is not null && ctx.SessionScope != "chat")
            return $"{prefix}{skillContent}\n\nFocus on ticket #{ctx.TicketId}: {ctx.TicketTitle}";
        return ctx.ExtraContext is null ? $"{prefix}{skillContent}" : $"{prefix}{skillContent}\n\n{ctx.ExtraContext}";
    }

    private static async Task<string> BuildPreambleAsync(ClaudeRunContext ctx, CancellationToken ct)
    {
        var sb = new StringBuilder();

        var preambleFile = Path.Combine(ctx.WorkspacePath, ".agents", "preamble.md");
        if (File.Exists(preambleFile))
        {
            var preamble = await File.ReadAllTextAsync(preambleFile, ct);
            sb.AppendLine(preamble.Replace("{agent}", ctx.AgentName));
            sb.AppendLine();
        }

        var memoryFile = Path.Combine(ctx.WorkspacePath, ".agents", ctx.AgentName, "memory.md");
        if (File.Exists(memoryFile))
        {
            sb.AppendLine(await File.ReadAllTextAsync(memoryFile, ct));
            sb.AppendLine();
        }

        return sb.ToString();
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
                // For assistant message events: emit the assistant text first, then separate tool_use events
                if (kind == "assistant" &&
                    doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    run.Push(new StreamEvent(DateTime.UtcNow, kind, FlattenJson(doc.RootElement)));
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var ptype) && ptype.GetString() == "tool_use")
                        {
                            var toolName = part.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                            var toolInput = part.TryGetProperty("input", out var inp) ? inp.ToString() : "{}";
                            run.Push(new StreamEvent(DateTime.UtcNow, "tool_use", toolName, toolInput));
                        }
                    }
                }
                else
                {
                    run.Push(new StreamEvent(DateTime.UtcNow, kind, FlattenJson(doc.RootElement)));
                }
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

    internal static string FlattenJson(JsonElement e)
    {
        // Produce a short human-readable line per event.
        if (e.ValueKind != JsonValueKind.Object) return e.ToString();
        var typePrefix = new StringBuilder();
        if (e.TryGetProperty("type", out var t)) typePrefix.Append('[').Append(t.GetString()).Append("] ");
        var body = new StringBuilder();
        if (e.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("text", out var dtext)) body.Append(dtext.GetString());
        }
        if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            if (m.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var pt2) && pt2.GetString() == "tool_use")
                        {
                            // tool_use parts are emitted as separate tool_use events — skip to avoid duplication
                        }
                        else if (part.TryGetProperty("text", out var text))
                        {
                            body.Append(text.GetString());
                        }
                        else if (part.TryGetProperty("type", out var pt) && pt.GetString() == "tool_result" &&
                                 part.TryGetProperty("content", out var tc))
                        {
                            if (tc.ValueKind == JsonValueKind.String)
                                body.Append(tc.GetString());
                            else if (tc.ValueKind == JsonValueKind.Array)
                                foreach (var tcp in tc.EnumerateArray())
                                    if (tcp.TryGetProperty("text", out var tt)) body.Append(tt.GetString());
                        }
                    }
                }
                else if (content.ValueKind == JsonValueKind.String)
                {
                    body.Append(content.GetString());
                }
            }
        }
        if (body.Length == 0) return typePrefix.Length > 0 ? typePrefix.ToString() : e.ToString();
        return typePrefix.Append(body).ToString();
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
        catch { /* best-effort debug log — disk errors must not crash the run */ }
    }
}
