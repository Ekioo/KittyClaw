using System.Text.Json;

namespace KittyClaw.ClaudeMock;

internal static class ScenarioReplayer
{
    public static async Task<int> ReplayAsync(string[] lines, string? sessionId, string workingDirectory)
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
                    if (meta.TryGetProperty("write_file", out var write) &&
                        write.TryGetProperty("path", out var pathElement) &&
                        write.TryGetProperty("content", out var contentElement))
                    {
                        var relativePath = pathElement.GetString();
                        if (!string.IsNullOrWhiteSpace(relativePath))
                        {
                            var fullPath = Path.GetFullPath(Path.Combine(workingDirectory, relativePath));
                            var rootPath = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
                            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Mock scenario write_file must stay inside the working directory.");
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                            await File.WriteAllTextAsync(fullPath, contentElement.GetString() ?? string.Empty);
                        }
                    }
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
