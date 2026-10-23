using System.Text.Json;

namespace KittyClaw.Core.Tests.Performance;

public sealed class BenchmarkComparisonTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "kc-perf-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(100, 100, 0)]
    [InlineData(125, 125, 0)]
    [InlineData(126, 100, 1)]
    [InlineData(100, 126, 1)]
    [InlineData(80, 80, 0)]
    public void DetectsTimeAndAllocationRegressions(double time, double bytes, int expected)
    {
        Assert.Equal(expected, Compare(Make(), Make(time, bytes)));
    }

    [Fact]
    public void NewAllocationFromZeroFails() =>
        Assert.Equal(1, Compare(Make(bytes: 0), Make(bytes: 1)));

    [Fact]
    public void EnvironmentMismatchIsAnError() =>
        Assert.Equal(2, Compare(Make(), Make() with { Architecture = "other" }));

    [Fact]
    public void MissingScenarioIsAnError() =>
        Assert.Equal(2, Compare(Make(), Make() with { Measurements = [] }));

    [Fact]
    public void RenamedScenarioIsAnError()
    {
        var current = Make();
        current.Measurements[0] = current.Measurements[0] with { Name = "different" };
        Assert.Equal(2, Compare(Make(), current));
    }

    [Fact]
    public void NegativeMetricIsAnError() =>
        Assert.Equal(2, Compare(Make(), Make(bytes: -1)));

    [Fact]
    public void AllocationScopeMismatchIsAnError()
    {
        var current = Make();
        current.Measurements[0] = current.Measurements[0] with { AllocationScope = "process" };
        Assert.Equal(2, Compare(Make(), current));
    }

    [Fact]
    public void OldSuiteRequiresANewBaseline() =>
        Assert.Equal(2, Compare(Make() with { Suite = "core-v1" }, Make()));

    private static Report Make(double time = 100, double bytes = 100) =>
        new(BenchmarkProgram.Suite, "test-runtime", "test-os", "test-arch", 4, DateTime.UnixEpoch,
            [new("workload", time, bytes, 10, Enumerable.Repeat(time, 9).ToArray())]);

    private int Compare(Report baseline, Report current)
    {
        Directory.CreateDirectory(directory);
        var before = Path.Combine(directory, "baseline.json");
        var after = Path.Combine(directory, "current.json");
        File.WriteAllText(before, JsonSerializer.Serialize(baseline));
        File.WriteAllText(after, JsonSerializer.Serialize(current));
        return BenchmarkProgram.Run(["compare", before, after, "25"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
