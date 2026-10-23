using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Logs per-run cost events and enforces a daily budget cap per workspace.
/// Appends to `.agents/channel/cost-log.jsonl` and rotates monthly.
/// </summary>
public sealed class CostTracker
{
    private readonly object _lock = new();
    private readonly SessionRegistry? _sessions;

    public CostTracker(SessionRegistry? sessions = null) => _sessions = sessions;

    public bool IsBudgetExceeded(string workspacePath, decimal dailyBudgetUsd)
    {
        if (dailyBudgetUsd <= 0) return false;
        var today = DateTime.UtcNow.Date;
        var total = SumUsdForDay(workspacePath, today);
        return total >= dailyBudgetUsd;
    }

    public void LogRun(string workspacePath, CostLogEntry entry)
    {
        var path = CurrentLogPath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (_lock)
        {
            RotateIfNeeded(path);
            var line = JsonSerializer.Serialize(entry) + "\n";
            File.AppendAllText(path, line);
        }
    }

    /// <summary>Includes known usage not yet persisted by the asynchronous completion recorder.</summary>
    internal decimal GetDailySpendUsd(string workspacePath, IEnumerable<AgentRun> runs) =>
        SumUsdForDay(workspacePath, DateTime.UtcNow.Date, runs);

    internal string CurrentLogPath(string workspacePath) =>
        _sessions?.ChannelFilePath(workspacePath, "cost-log.jsonl")
        ?? Path.Combine(workspacePath, ".agents", "channel", "cost-log.jsonl");

    internal IReadOnlyList<string> LogFiles(string workspacePath) =>
        _sessions?.ChannelFiles(workspacePath, "cost-log*.jsonl")
        ?? (Directory.Exists(Path.Combine(workspacePath, ".agents", "channel"))
            ? Directory.EnumerateFiles(Path.Combine(workspacePath, ".agents", "channel"), "cost-log*.jsonl").ToList()
            : []);

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path)) return;
        var lines = File.ReadLines(path).Count();
        if (lines < 5000) return;
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var rotated = path.Replace("cost-log.jsonl", $"cost-log-{month}.jsonl");
        if (!File.Exists(rotated)) File.Move(path, rotated);
        else
        {
            // append and clear current
            File.AppendAllLines(rotated, File.ReadAllLines(path));
            File.WriteAllText(path, string.Empty);
        }
    }

    private decimal SumUsdForDay(string workspacePath, DateTime day, IEnumerable<AgentRun>? runs = null)
    {
        lock (_lock) return SumUsdForDayCore(workspacePath, day, runs);
    }

    private decimal SumUsdForDayCore(string workspacePath, DateTime day, IEnumerable<AgentRun>? runs)
    {
        var files = LogFiles(workspacePath);
        decimal sum = 0m;
        var entries = new List<CostLogEntry>();
        foreach (var path in files.Order(StringComparer.Ordinal))
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            CostLogEntry? e;
            try { e = JsonSerializer.Deserialize<CostLogEntry>(line); }
            catch { /* skip malformed lines */ continue; }
            if (e is null) continue;
            entries.Add(e);
        }
        var latestEntries = entries.Where(entry => entry.RunId is null)
                     .Concat(entries.Where(entry => entry.RunId is not null).GroupBy(entry => entry.RunId!)
                         .Select(group => group.OrderBy(entry => entry.UsageVersion).Last())).ToList();
        foreach (var entry in latestEntries)
            if (entry.At.Date == day && entry.CostKnown) sum += Math.Max(0m, entry.UsdCost);
        foreach (var run in runs ?? [])
        {
            if ((run.EndedAt ?? DateTime.UtcNow).Date != day || !run.UsageIsReliable || run.TotalCostUsd is not decimal cost) continue;
            var recorded = latestEntries.Where(entry => entry.At.Date == day && entry.CostKnown && entry.Source != "jev"
                && entry.RunId == run.RunId && (entry.StartedAt == run.StartedAt || entry.StartedAt is null && entry.At == run.EndedAt))
                .Sum(entry => Math.Max(0m, entry.UsdCost));
            sum += Math.Max(0m, cost - recorded);
        }
        return sum;
    }
}

public sealed record CostLogEntry(
    DateTime At,
    string Agent,
    int? TicketId,
    string Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    decimal UsdCost,
    double DurationSeconds,
    int ExitCode,
    bool CostEstimated = false,
    string? ProjectSlug = null,
    int? PipelineId = null,
    string? RunId = null,
    string? Source = null,
    bool CostKnown = true,
    string? SessionId = null,
    DateTime? StartedAt = null,
    int UsageVersion = 0,
    string? BillingPlan = null,
    string? RateCardVersion = null,
    bool UsageReliable = true
);
