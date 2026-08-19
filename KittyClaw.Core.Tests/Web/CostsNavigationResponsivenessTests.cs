using KittyClaw.Web.Components.Pages;

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
    public void CostPage_UsesCompactProjectPickerAndProjectScopedCosts()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));

        Assert.Contains("<details class=\"cost-project-picker\">", source, StringComparison.Ordinal);
        Assert.Contains("@foreach (var project in _projectCosts)", source, StringComparison.Ordinal);
        Assert.Contains("bucket.ProjectSlug == project.ProjectSlug", source, StringComparison.Ordinal);
        Assert.Contains("class=\"cost-project-total\"", source, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"cost-visible-total\"", source, StringComparison.Ordinal);
        Assert.Contains("CostDisplayTotal.Sum(_projectCosts.Select(project => project.UsdCost))", source, StringComparison.Ordinal);
        Assert.Contains("_projectCosts.Any(project => project.Estimated)", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("data-testid=\"cost-visible-total\"", StringComparison.Ordinal)
            < source.IndexOf("@if (_report.Projects.Count == 0)", StringComparison.Ordinal),
            "The displayed total must render for both non-empty and empty report states.");
    }

    [Fact]
    public void CostPage_PlacesProjectsBeforePipelineAndUsesProjectScopedPipelineOptions()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));

        var projectsIndex = source.IndexOf("<details class=\"cost-project-picker\">", StringComparison.Ordinal);
        var pipelineIndex = source.IndexOf("<select class=\"cost-pipeline\"", StringComparison.Ordinal);

        Assert.True(projectsIndex >= 0 && projectsIndex < pipelineIndex);
        Assert.Contains("@foreach (var pipeline in AvailablePipelines)", source, StringComparison.Ordinal);
        Assert.Contains("ValidatePipelineSelection(); await Reload();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CostPage_OffersModelFilterAndExplainsChartEncoding()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "css", "costs.css"));

        Assert.Contains("class=\"cost-model\"", source, StringComparison.Ordinal);
        Assert.Contains("@foreach (var model in _options?.Models ?? [])", source, StringComparison.Ordinal);
        Assert.Contains("OnModelChanged", source, StringComparison.Ordinal);
        Assert.Contains("class=\"cost-legend\"", source, StringComparison.Ordinal);
        Assert.Contains("cost-legend-measured", styles, StringComparison.Ordinal);
        Assert.Contains("cost-legend-estimated", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void CostPage_ShowsProjectScopedRtkSavingsWithAnExplicitEstimateLegend()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "css", "costs.css"));

        Assert.Contains("project.RtkSavedTokens > 0", source, StringComparison.Ordinal);
        Assert.Contains("class=\"cost-project-rtk\"", source, StringComparison.Ordinal);
        Assert.Contains("RtkSavingsHelp", source, StringComparison.Ordinal);
        Assert.Contains("cost-legend-rtk", styles, StringComparison.Ordinal);
        Assert.Contains("_report.Projects.Count == 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CostPage_StacksMeasuredEstimatedAndRtkSegmentsPerDay()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "css", "costs.css"));

        Assert.Contains("cost-bar-measured", source, StringComparison.Ordinal);
        Assert.Contains("cost-bar-estimated", source, StringComparison.Ordinal);
        Assert.Contains("cost-bar-rtk", source, StringComparison.Ordinal);
        // Honest fallback: a day with saved tokens but no known model rate shows a marker, never a fabricated bar.
        Assert.Contains("cost-bar-rtk-unpriced", source, StringComparison.Ordinal);
        Assert.Contains("RtkNoUsdEstimate", source, StringComparison.Ordinal);
        Assert.Contains("bucket.UsdCost + (bucket.RtkEstimatedUsd ?? 0)", source, StringComparison.Ordinal);
        Assert.Contains(".cost-bar-measured", styles, StringComparison.Ordinal);
        Assert.Contains(".cost-bar-estimated", styles, StringComparison.Ordinal);
        Assert.Contains(".cost-bar-rtk", styles, StringComparison.Ordinal);
        Assert.Contains(".cost-bar-rtk-unpriced", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void CostPage_OffersQuickDateRangesAndKeepsManualDatesCustom()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Costs.razor"));

        Assert.Contains("DatePreset.CurrentMonth", source, StringComparison.Ordinal);
        Assert.Contains("DatePreset.PreviousMonth", source, StringComparison.Ordinal);
        Assert.Contains("DatePreset.LastThreeMonths", source, StringComparison.Ordinal);
        Assert.Contains("DatePreset.CurrentYear", source, StringComparison.Ordinal);
        Assert.Contains("_activePreset = null", source, StringComparison.Ordinal);
        Assert.Contains("await Reload();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CostPageStyles_ContainResponsiveLayoutAndBoundedOverflow()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "wwwroot", "css", "costs.css"));

        Assert.Contains(".cost-project-card-grid", source, StringComparison.Ordinal);
        Assert.Contains(".cost-visible-total", source, StringComparison.Ordinal);
        Assert.Contains(".cost-date-preset.is-active", source, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 560px)", source, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayedTotal_SumsTheRoundedAmountsShownOnProjectCards()
    {
        var total = CostDisplayTotal.Sum([0.034m, 0.034m]);

        Assert.Equal(0.06m, total);
    }

    [Fact]
    public void DisplayedTotal_IsZeroWhenNoProjectCardIsVisible()
    {
        Assert.Equal(0m, CostDisplayTotal.Sum([]));
    }

    [Fact]
    public void EnablingRtk_RequestsAnAsynchronousCostSnapshotRefresh()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Api", "Endpoints.Projects.cs"));

        Assert.Contains("CostReportService costReports", source, StringComparison.Ordinal);
        Assert.Contains("costReports.RequestRefresh();", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
