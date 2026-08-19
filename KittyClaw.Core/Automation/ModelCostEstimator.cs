namespace KittyClaw.Core.Automation;

/// <summary>
/// Estimates provider cost when a CLI reports token usage but no monetary total.
/// Rates are deliberately explicit and versioned: unknown models remain unpriced rather
/// than silently inheriting a possibly incorrect family rate. Provider-reported costs
/// always take precedence in the stream adapters.
/// </summary>
public static class ModelCostEstimator
{
    public const string RateCardVersion = "2026-08-19";

    public static bool TryEstimate(
        string? model,
        int inputTokens,
        int outputTokens,
        int cacheReadTokens,
        int cacheWriteTokens,
        out decimal costUsd)
    {
        costUsd = 0m;
        if (string.IsNullOrWhiteSpace(model)) return false;

        var normalized = model.Trim();
        if (normalized.StartsWith("codex:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["codex:".Length..];
        if (normalized.StartsWith("mistral:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["mistral:".Length..];

        Rates? rates = normalized.ToLowerInvariant() switch
        {
            "gpt-5.6-sol" => new(5m, 30m, 0.5m, 6.25m),
            "gpt-5.6-terra" => new(2.5m, 15m, 0.25m, 3.125m),
            "gpt-5.6-luna" => new(1m, 6m, 0.1m, 1.25m),
            "gpt-5.5" => new(5m, 30m, 0.5m, 6.25m),
            "gpt-5.4" => new(2.5m, 15m, 0.25m, 3.125m),
            "grok-4.5" or "grok-4.5-latest" => new(2m, 6m, 0.3m, 2m),
            "grok-build-0.1" => new(1m, 2m, 0.2m, 1m),
            "mistral-medium-3.5" or "mistral-vibe-cli-latest" => new(1.5m, 7.5m, 0.15m, 1.5m),
            "devstral-small" or "devstral-small-latest" => new(0.1m, 0.3m, 0.01m, 0.1m),
            _ => null,
        };
        if (rates is null) return false;

        // xAI doubles all token rates for requests whose context reaches 200k tokens.
        var inputContext = (long)inputTokens + cacheReadTokens + cacheWriteTokens;
        if (normalized.StartsWith("grok-", StringComparison.OrdinalIgnoreCase) && inputContext >= 200_000)
            rates = rates.Value.Scale(2m);

        costUsd = (
            inputTokens * rates.Value.Input
            + outputTokens * rates.Value.Output
            + cacheReadTokens * rates.Value.CacheRead
            + cacheWriteTokens * rates.Value.CacheWrite) / 1_000_000m;
        return true;
    }

    private readonly record struct Rates(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)
    {
        public Rates Scale(decimal factor) =>
            new(Input * factor, Output * factor, CacheRead * factor, CacheWrite * factor);
    }
}
