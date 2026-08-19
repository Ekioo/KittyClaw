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
        Assert.DoesNotContain("class=\"cost-total-card", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@Usd(_report.TotalUsd)", source, StringComparison.Ordinal);
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
        Assert.Contains(".cost-date-preset.is-active", source, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 560px)", source, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
