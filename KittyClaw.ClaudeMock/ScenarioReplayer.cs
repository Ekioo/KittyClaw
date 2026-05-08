using System.Text.Json;

namespace KittyClaw.ClaudeMock;

internal static class ScenarioReplayer
{
    public static async Task<int> ReplayAsync(string[] lines, string? sessionId)
    {
        int exitCode = 0;
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
}
