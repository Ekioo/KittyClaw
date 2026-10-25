using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ModelCostEstimatorTests
{
    [Theory]
    [InlineData("gpt-5.6-sol", 4, 20, 0.4, 5)]
    [InlineData("codex:gpt-5.6-terra", 2.5, 15, 0.25, 3.125)]
    [InlineData("gpt-5.6-luna", 1, 6, 0.1, 1.25)]
    [InlineData("codex:gpt-5.5", 5, 30, 0.5, 6.25)]
    [InlineData("gpt-5.4", 2.5, 15, 0.25, 3.125)]
    public void OpenAiRates_AreAppliedPerTokenClass(
        string model, double input, double output, double cacheRead, double cacheWrite)
    {
        Assert.True(ModelCostEstimator.TryEstimate(
            model, 1_000_000, 1_000_000, 1_000_000, 1_000_000, out var cost));

        Assert.Equal((decimal)(input + output + cacheRead + cacheWrite), cost);
    }

    [Fact]
    public void Grok45_LongContext_DoublesRates()
    {
        Assert.True(ModelCostEstimator.TryEstimate(
            "grok-4.5", 200_000, 10_000, 0, 0, out var cost));

        Assert.Equal(0.92m, cost);
    }

    [Fact]
    public void GrokBuild01_UsesItsOwnRateCard()
    {
        Assert.True(ModelCostEstimator.TryEstimate(
            "grok-build-0.1", 100_000, 10_000, 50_000, 0, out var cost));

        Assert.Equal(0.13m, cost);
    }

    [Theory]
    [InlineData("grok-4.7")]
    [InlineData("grok-4.6")]
    public void CurrentGrokModels_PriceCachedInputAndLongContext(string model)
    {
        Assert.True(ModelCostEstimator.TryEstimate(
            model, 100_000, 10_000, 50_000, 0, out var shortCost));
        Assert.Equal(0.285m, shortCost);
        Assert.True(ModelCostEstimator.TryEstimate(
            model, 150_000, 10_000, 50_000, 0, out var longCost));
        Assert.Equal(0.77m, longCost);
    }

    [Theory]
    [InlineData("mistral:mistral-medium-3.5", 10.65)]
    [InlineData("mistral:devstral-small", 0.51)]
    public void MistralVibeAliases_UseOfficialRateCard(string model, double expected)
    {
        Assert.True(ModelCostEstimator.TryEstimate(
            model, 1_000_000, 1_000_000, 1_000_000, 1_000_000, out var cost));
        Assert.Equal((decimal)expected, cost);
    }

    [Theory]
    [InlineData("deepseek:deepseek-v4-pro[1m]", 6.644)]
    [InlineData("deepseek:deepseek-v4-flash", 1.806)]
    [InlineData("deepseek:deepseek-flash", 1.806)]
    public void DeepSeekV4Models_UseOfficialRateCard(string model, double expected)
    {
        Assert.True(ModelCostEstimator.TryEstimate(
            model, 1_000_000, 1_000_000, 1_000_000, 1_000_000, out var cost));
        Assert.Equal((decimal)expected, cost);
    }

    [Fact]
    public void ClaudeHaiku_ContextThresholdIncludesCachedInput()
    {
        Assert.True(ModelCostEstimator.TryEstimate("claude:claude-haiku-5-5",
            50_000, 10_000, 50_000, 0, out var shortCost));
        Assert.Equal(0.0105m, shortCost);
        Assert.True(ModelCostEstimator.TryEstimate("claude:claude-haiku-5-5",
            50_001, 10_000, 50_000, 0, out var longCost));
        Assert.Equal(0.0525005m, longCost);
    }

    [Theory]
    [InlineData("claude-fable-5-1", 72.75)]
    [InlineData("claude:claude-opus-5-5", 29.2)]
    [InlineData("claude-sonnet-5-5", 14.6)]
    public void ClaudeRates_PriceAllTokenClasses(string model, double expected)
    {
        Assert.True(ModelCostEstimator.TryEstimate(model,
            1_000_000, 1_000_000, 1_000_000, 1_000_000, out var cost));
        Assert.Equal((decimal)expected, cost);
    }

    [Fact]
    public void UnknownModel_IsNotGuessed()
    {
        Assert.False(ModelCostEstimator.TryEstimate(
            "future-model", 1_000, 1_000, 0, 0, out var cost));
        Assert.Equal(0m, cost);
    }
}
