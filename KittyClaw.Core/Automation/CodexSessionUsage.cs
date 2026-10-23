using System.Text.Json;

namespace KittyClaw.Core.Automation;

internal readonly record struct CodexTokenTotals(int Input, int Cached, int Output, int Writes = 0)
{
    public CodexTokenTotals Delta(CodexTokenTotals previous) => new(
        Math.Max(0, Input - previous.Input), Math.Max(0, Cached - previous.Cached),
        Math.Max(0, Output - previous.Output), Math.Max(0, Writes - previous.Writes));
    public static CodexTokenTotals From(JsonElement value) => new(
        Read(value, "input_tokens"), Read(value, "cached_input_tokens"),
        Read(value, "output_tokens"), Read(value, "cache_write_input_tokens"));
    private static int Read(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.TryGetInt32(out var count) ? Math.Max(0, count) : 0;
}

internal sealed record CodexUsagePoint(DateTime At, CodexTokenTotals Total, string? Plan);

/// <summary>Reads only token telemetry from local rollouts, never authentication secrets or prompts.</summary>
internal sealed class CodexSessionUsage(string? root = null)
{
    private readonly string _root = root ?? Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "sessions");
    private readonly bool _includeArchives = root is null;
    private Dictionary<string, string>? _files;

    public IReadOnlyList<CodexUsagePoint> Read(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out _)) return [];
        try
        {
            var roots = _includeArchives
                ? new[] { _root, Path.Combine(Path.GetDirectoryName(_root)!, "archived_sessions") }
                : [_root];
            _files ??= roots.Where(Directory.Exists).SelectMany(directory =>
                    Directory.EnumerateFiles(directory, "rollout-*.jsonl", SearchOption.AllDirectories))
                    .Where(path => Path.GetFileNameWithoutExtension(path).Length >= 36)
                    .GroupBy(path => Path.GetFileNameWithoutExtension(path)[^36..], StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            if (!_files.TryGetValue(sessionId, out var file)) return [];
            var points = new List<CodexUsagePoint>();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"token_count\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var value = document.RootElement;
                    if (!value.TryGetProperty("payload", out var payload)
                        || !payload.TryGetProperty("type", out var type) || type.GetString() != "token_count"
                        || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                        || !info.TryGetProperty("total_token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object
                        || !value.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetDateTime(out var at)) continue;
                    string? plan = null;
                    if (payload.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object
                        && limits.TryGetProperty("plan_type", out var planValue) && planValue.ValueKind == JsonValueKind.String)
                        plan = planValue.GetString();
                    points.Add(new(at.ToUniversalTime(), CodexTokenTotals.From(usage), plan));
                }
                catch (JsonException) { /* An actively appended final line may be incomplete. */ }
            }
            return points;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static CodexTokenTotals? Before(IReadOnlyList<CodexUsagePoint> points, DateTime at) =>
        points.LastOrDefault(point => point.At < at)?.Total;

    public static (CodexTokenTotals Tokens, string? Plan)? Between(
        IReadOnlyList<CodexUsagePoint> points, DateTime start, DateTime end)
    {
        var sum = default(CodexTokenTotals);
        var found = false;
        string? plan = null;
        foreach (var point in Deltas(points, start, end))
        {
            var delta = point.Total;
            found = true;
            plan = point.Plan ?? plan;
            sum = new(checked(sum.Input + delta.Input), checked(sum.Cached + delta.Cached),
                checked(sum.Output + delta.Output), checked(sum.Writes + delta.Writes));
        }
        return found ? (sum, plan) : null;
    }

    public static IEnumerable<CodexUsagePoint> Deltas(IReadOnlyList<CodexUsagePoint> points, DateTime start, DateTime end)
    {
        var previous = default(CodexTokenTotals);
        foreach (var point in points)
        {
            if (point.At > end) yield break;
            var delta = point.Total.Input < previous.Input || point.Total.Output < previous.Output
                ? point.Total : point.Total.Delta(previous);
            previous = point.Total;
            if (point.At > start) yield return point with { Total = delta };
        }
    }
}
