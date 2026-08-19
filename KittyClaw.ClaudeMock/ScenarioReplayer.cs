using System.Text.Json;

namespace KittyClaw.ClaudeMock;

internal static class ScenarioReplayer
{
    public static async Task<int> ReplayAsync(
        string[] lines, string? sessionId, string workingDirectory, HookSettings? hooks = null)
    {
        hooks ??= new HookSettings(null, null);
        int exitCode = 0;
        int hookedToolCounter = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Look for our extension fields without disturbing real-claude-compatible JSON.
            int delayMs = 0;
            string emit = line;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // _meta line: control envelope, do not emit on stdout
                if (root.TryGetProperty("_meta", out var meta))
                {
                    if (meta.TryGetProperty("exit", out var ex) && ex.TryGetInt32(out var code))
                        exitCode = code;
                    if (meta.TryGetProperty("delay_ms", out var d) && d.TryGetInt32(out var ms))
                        await Task.Delay(ms);
                    if (meta.TryGetProperty("write_file", out var write))
                        await WriteEffectFileAsync(write, workingDirectory);
                    if (meta.TryGetProperty("write_env", out var writeEnv) &&
                        writeEnv.TryGetProperty("path", out var envPathElement) &&
                        writeEnv.TryGetProperty("name", out var envNameElement))
                    {
                        var relativePath = envPathElement.GetString();
                        var environmentName = envNameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(relativePath) && !string.IsNullOrWhiteSpace(environmentName))
                        {
                            var fullPath = Path.GetFullPath(Path.Combine(workingDirectory, relativePath));
                            var rootPath = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
                            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Mock scenario write_env must stay inside the working directory.");
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                            await File.WriteAllTextAsync(fullPath,
                                Environment.GetEnvironmentVariable(environmentName) ?? string.Empty);
                        }
                    }
                    if (meta.TryGetProperty("emit_env", out var emitEnvironment))
                    {
                        var environmentName = emitEnvironment.GetString();
                        if (!string.IsNullOrWhiteSpace(environmentName))
                        {
                            await Console.Out.WriteLineAsync(Environment.GetEnvironmentVariable(environmentName) ?? string.Empty);
                            await Console.Out.FlushAsync();
                        }
                    }
                    if (meta.TryGetProperty("hooked_effect", out var hookedEffect))
                        await ReplayHookedEffectAsync(hookedEffect, sessionId, workingDirectory, hooks,
                            ++hookedToolCounter);
                    continue;
                }

                if (root.TryGetProperty("_delay_ms", out var dl) && dl.TryGetInt32(out var ms2))
                    delayMs = ms2;

                if (sessionId is not null && root.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    // Pass-through: scenarios may use a placeholder session id; rewrite if needed.
                    if (s.GetString() == "{{session_id}}")
                        emit = line.Replace("{{session_id}}", sessionId);
                }
            }
            catch
            {
                // Non-JSON lines (e.g. comments) are ignored — real claude never emits these,
                // so dropping them keeps the consumer's parser happy.
                continue;
            }

            await Console.Out.WriteLineAsync(emit);
            await Console.Out.FlushAsync();
            if (delayMs > 0) await Task.Delay(delayMs);
        }
        return exitCode;
    }

    private static async Task WriteEffectFileAsync(JsonElement write, string workingDirectory)
    {
        if (!write.TryGetProperty("path", out var pathElement) ||
            !write.TryGetProperty("content", out var contentElement))
            return;
        var relativePath = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var fullPath = Path.GetFullPath(Path.Combine(workingDirectory, relativePath));
        var rootPath = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mock scenario write_file must stay inside the working directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, contentElement.GetString() ?? string.Empty);
    }

    // Simulates a tool call the way real claude runs it when PreToolUse hooks are configured:
    // the hook decides BEFORE the effect. Allow → tool_use + effect + tool_result + PostToolUse;
    // anything else (deny, bad verdict, non-zero exit) → no effect. Without a configured hook the
    // effect runs freely, mirroring an unhooked real CLI.
    private static async Task ReplayHookedEffectAsync(
        JsonElement hookedEffect, string? sessionId, string workingDirectory, HookSettings hooks, int ordinal)
    {
        var toolName = hookedEffect.TryGetProperty("tool_name", out var name) ? name.GetString() ?? "Bash" : "Bash";
        var toolInput = hookedEffect.TryGetProperty("tool_input", out var input) ? input.GetRawText() : "{}";
        var payloadPrefix = $"{{\"session_id\":{JsonSerializer.Serialize(sessionId ?? "mock")}," +
            $"\"tool_name\":{JsonSerializer.Serialize(toolName)},\"tool_input\":{toolInput}";

        var allowed = hooks.PreToolUseCommand is null
            || await HookRunner.RunPreToolUseAsync(hooks.PreToolUseCommand,
                payloadPrefix + ",\"hook_event_name\":\"PreToolUse\"}");
        if (!allowed)
        {
            await EmitAsync($"{{\"type\":\"system\",\"subtype\":\"hook_denied\",\"tool\":{JsonSerializer.Serialize(toolName)}}}");
            return;
        }

        var toolUseId = $"toolu_hooked_{ordinal}";
        await EmitAsync("{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[" +
            $"{{\"type\":\"tool_use\",\"id\":\"{toolUseId}\",\"name\":{JsonSerializer.Serialize(toolName)},\"input\":{toolInput}}}]}}}}");
        if (hookedEffect.TryGetProperty("write_file", out var write))
            await WriteEffectFileAsync(write, workingDirectory);
        await EmitAsync("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[" +
            $"{{\"type\":\"tool_result\",\"tool_use_id\":\"{toolUseId}\",\"content\":\"ok\"}}]}}}}");

        if (hooks.PostToolUseCommand is not null)
            await HookRunner.RunPostToolUseAsync(hooks.PostToolUseCommand,
                payloadPrefix + ",\"hook_event_name\":\"PostToolUse\",\"tool_response\":{\"success\":true}}");
    }

    private static async Task EmitAsync(string line)
    {
        await Console.Out.WriteLineAsync(line);
        await Console.Out.FlushAsync();
    }
}
