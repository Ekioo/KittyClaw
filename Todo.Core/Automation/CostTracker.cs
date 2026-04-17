using System.Text.Json;

namespace Todo.Core.Automation;

/// <summary>
/// Logs per-run cost events and enforces a daily budget cap per workspace.
/// Appends to `.agents/channel/cost-log.jsonl` and rotates monthly.
/// </summary>
public sealed class CostTracker
{
    private readonly object _lock = new();

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

    private static string CurrentLogPath(string workspacePath) =>
        Path.Combine(workspacePath, ".agents", "channel", "cost-log.jsonl");

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

    private static decimal SumUsdForDay(string workspacePath, DateTime day)
    {
        var path = CurrentLogPath(workspacePath);
        if (!File.Exists(path)) return 0m;
        decimal sum = 0m;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            CostLogEntry? e;
            try { e = JsonSerializer.Deserialize<CostLogEntry>(line); }
            catch { continue; }
            if (e is null) continue;
            if (e.At.Date != day) continue;
            sum += e.UsdCost;
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
    int ExitCode
);
