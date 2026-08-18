namespace KittyClaw.Core.Tests.Web;

public sealed class CostsNavigationResponsivenessTests
{
    [Fact]
    public void CostDataLoading_UsesStreamingRendering()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));

        Assert.Contains("@attribute [StreamRendering]", source, StringComparison.Ordinal);
        Assert.Contains("OnInitializedAsync", source, StringComparison.Ordinal);
        Assert.Contains("_options = await CostReports.GetOptionsAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CostPage_UsesCompactProjectPickerAndDailyAggregatedChart()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));

        Assert.Contains("<details class=\"cost-project-picker\">", source, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(bucket => bucket.Day)", source, StringComparison.Ordinal);
        Assert.Contains("@foreach (var day in DailyTotals)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@foreach (var bucket in _report.Daily)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CostPageStyles_ContainResponsiveLayoutAndBoundedOverflow()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "css", "costs.css"));

        Assert.Contains(".cost-chart-scroll{min-width:0;overflow-x:auto", source, StringComparison.Ordinal);
        Assert.Contains("@media(max-width:560px)", source, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion:reduce", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
