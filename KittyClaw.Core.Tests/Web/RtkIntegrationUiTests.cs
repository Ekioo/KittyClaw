using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class RtkIntegrationUiTests
{
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void ProjectSettings_ContainsExplicitRtkOptInAndRuntimeStatus()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "ProjectSettings.razor"));

        Assert.Contains("data-testid=\"rtk-settings\"", source);
        Assert.Contains("data-testid=\"rtk-enabled\"", source);
        Assert.Contains("data-testid=\"rtk-runtime-status\"", source);
        Assert.Contains("/api/projects/{Slug}/rtk", source);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    public void ProjectSettings_LocalizationsContainRtkCopy(string language)
    {
        var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"ProjectSettings.{language}.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        Assert.True(json.RootElement.TryGetProperty("RtkTitle", out _));
        Assert.True(json.RootElement.TryGetProperty("RtkEnableEffect", out _));
        Assert.True(json.RootElement.TryGetProperty("RtkUnavailable", out _));
    }
}
