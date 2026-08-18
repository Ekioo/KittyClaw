namespace KittyClaw.Core.Tests.Web;

public class LegacyAutomationUiRemovalTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Theory]
    [InlineData("Board.razor")]
    [InlineData("UnifiedBoard.razor")]
    [InlineData("Dashboard.razor")]
    [InlineData("ProjectSettings.razor")]
    public void ProjectNavigation_DoesNotExposeLegacyAutomations(string page)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", page));

        Assert.DoesNotContain("/automations", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenAutomationsEditor", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAutomationsPageAndStyles_AreRemoved()
    {
        var root = RepoRoot();

        Assert.False(File.Exists(Path.Combine(root, "KittyClaw.Web", "Components", "Pages", "Automations.razor")));
        Assert.False(File.Exists(Path.Combine(root, "KittyClaw.Web", "wwwroot", "css", "automations.css")));

        var app = File.ReadAllText(Path.Combine(root, "KittyClaw.Web", "Components", "App.razor"));
        Assert.DoesNotContain("css/automations.css", app, StringComparison.OrdinalIgnoreCase);
    }
}
