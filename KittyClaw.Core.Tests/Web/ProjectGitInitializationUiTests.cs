using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

public sealed class ProjectGitInitializationUiTests
{
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void ProjectSettings_OffersConfirmedInitializationAndRefreshesStatus()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "Pages", "ProjectSettings.razor"));

        Assert.Contains("data-testid=\"git-init\"", source);
        Assert.Contains("data-testid=\"git-init-confirm\"", source);
        Assert.Contains("/api/projects/{Slug}/git/init", source);
        Assert.Contains("await RefreshGitStatus()", source);
        Assert.Contains("_gitStatus.WorkspaceConfigured", source);
    }

    [Fact]
    public void ProjectsApi_ExposesStatusAndSafeInitEndpoints()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Api", "Endpoints.Projects.cs"));

        Assert.Contains("/projects/{slug}/git", source);
        Assert.Contains("/projects/{slug}/git/init", source);
        Assert.Contains("GitRepositoryInitializationService", source);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("ja")]
    [InlineData("pt-BR")]
    public void ProjectSettings_LocalizationsContainGitInitializationCopy(string language)
    {
        var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"ProjectSettings.{language}.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        Assert.True(json.RootElement.TryGetProperty("GitRepositoryMissing", out _));
        Assert.True(json.RootElement.TryGetProperty("GitInitializeConfirm", out _));
        Assert.True(json.RootElement.TryGetProperty("GitInitialized", out _));
    }
}
