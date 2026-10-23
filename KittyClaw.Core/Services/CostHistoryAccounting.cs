using System.Text.Json;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Services;

/// <summary>Rebuilds legacy Codex charges from request telemetry without modifying the source journal.</summary>
internal sealed class CostHistoryAccounting(string dataDir, CodexSessionUsage? sessions = null)
{
    private readonly CodexSessionUsage _sessions = sessions ?? new();
    private readonly Dictionary<string, IReadOnlyList<CodexUsagePoint>> _points = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CostLogEntry Normalize(CostLogEntry entry)
    {
        var model = entry.Model.StartsWith("codex:", StringComparison.OrdinalIgnoreCase) ? entry.Model[6..] : entry.Model;
        var codex = model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) && entry.Source != "jev"
            && (entry.SessionId is not null || Guid.TryParse(entry.RunId, out _));
        if (codex && entry.UsageVersion < 2)
        {
            if (entry.CostKnown && !entry.CostEstimated && entry.UsdCost > 0) return entry;
            var session = entry.SessionId;
            var start = entry.StartedAt;
            var end = entry.At;
            if (entry.RunId is { } id && Guid.TryParse(id, out _))
            {
                try
                {
                    var path = Path.Combine(dataDir, "runs", id + ".json");
                    var run = JsonSerializer.Deserialize<AgentRunSnapshot>(File.ReadAllText(path), JsonOptions);
                    if (run is not null)
                    {
                        session ??= run.SessionId;
                        start ??= run.StartedAt;
                        end = run.EndedAt ?? end;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            if (session is null || start is null)
                return entry with { CostKnown = false, UsdCost = 0, CostEstimated = false };
            if (!_points.TryGetValue(session, out var points)) _points[session] = points = _sessions.Read(session);
            var recovered = CodexSessionUsage.Between(points, start.Value, end);
            if (recovered is not { } usage)
                return entry with { CostKnown = false, UsdCost = 0, CostEstimated = false };
            entry = entry with
            {
                InputTokens = Math.Max(0, usage.Tokens.Input - usage.Tokens.Cached),
                OutputTokens = usage.Tokens.Output,
                CacheReadTokens = usage.Tokens.Cached,
                CacheWriteTokens = usage.Tokens.Writes,
                SessionId = session, StartedAt = start, UsageVersion = 2, BillingPlan = usage.Plan,
                CostEstimated = true, CostKnown = false
            };
            decimal requestCost = 0;
            var priced = true;
            foreach (var point in CodexSessionUsage.Deltas(points, start.Value, end))
            {
                var tokens = point.Total;
                priced &= ModelCostEstimator.TryEstimate(entry.Model, Math.Max(0, tokens.Input - tokens.Cached),
                    tokens.Output, tokens.Cached, tokens.Writes, out var amount, requestContext: true);
                requestCost += amount;
            }
            return entry with { UsdCost = priced ? requestCost : 0, CostKnown = priced, CostEstimated = priced,
                RateCardVersion = ModelCostEstimator.RateCardVersion };
        }
        if (!entry.UsageReliable) return entry with { CostKnown = false, UsdCost = 0, CostEstimated = false };
        if ((entry.UsageVersion >= 2 && entry.CostEstimated && entry.RateCardVersion != ModelCostEstimator.RateCardVersion
                || !entry.CostKnown || entry.UsdCost == 0)
            && (entry.InputTokens > 0 || entry.OutputTokens > 0 || entry.CacheReadTokens > 0 || entry.CacheWriteTokens > 0)
            && ModelCostEstimator.TryEstimate(entry.Model, entry.InputTokens, entry.OutputTokens,
                entry.CacheReadTokens, entry.CacheWriteTokens, out var usd))
            return entry with { UsdCost = usd, CostKnown = true, CostEstimated = true,
                RateCardVersion = ModelCostEstimator.RateCardVersion };
        if (entry.UsdCost == 0 && (entry.InputTokens > 0 || entry.OutputTokens > 0
                || entry.CacheReadTokens > 0 || entry.CacheWriteTokens > 0))
            return entry with { CostKnown = false };
        return entry;
    }
}
