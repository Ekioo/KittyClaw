using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ModelCostEstimatorTests
{
    [Theory]
    [InlineData("gpt-5.6-sol", 5, 30, 0.5, 6.25)]
    [InlineData("codex:gpt-5.6-terra", 2.5, 15, 0.25, 3.125)]
    [InlineData("gpt-5.6-luna", 1, 6, 0.1, 1.25)]
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

    [Fact]
    public void UnknownModel_IsNotGuessed()
    {
        Assert.False(ModelCostEstimator.TryEstimate(
            "future-model", 1_000, 1_000, 0, 0, out var cost));
        Assert.Equal(0m, cost);
    }
}
