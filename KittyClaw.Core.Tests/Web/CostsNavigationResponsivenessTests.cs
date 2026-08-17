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

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
