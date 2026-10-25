using System.Text.Json;

namespace KittyClaw.Core.Tests.Web;

[Collection("MCP endpoint environment")]
public sealed class ProjectCreationGitInitializationUiTests
{
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public async Task ProjectCreation_RendersWithoutLegacyMemberInitialization()
    {
        using var factory = new Api.McpEndpointTests.ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        Assert.Contains("data-testid=\"create-project-open\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void ProjectCreation_OffersGitInitializationOnlyForEligibleWorkspace()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "KittyClaw.Web", "Components", "ProjectCreation.razor"));

        Assert.Contains("data-testid=\"create-project-git-option\"", source);
        Assert.Contains("data-testid=\"create-project-git-init\"", source);
        Assert.Contains("_gitWorkspace?.CanInitialize == true", source);
        Assert.Contains("private bool _initializeGit = true", source);
        Assert.Contains("inspectionVersion == _workspaceInspectionVersion", source);
        Assert.Contains("InitializeAsync(_newWorkspacePath.Trim(), overwrite, initializeGit)", source);
        Assert.DoesNotContain("MemberService", source);
        Assert.DoesNotContain("EnsureAgentMembers", source);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("ja")]
    [InlineData("pt-BR")]
    [InlineData("zh-CN")]
    public void HomeLocalizations_ContainProjectCreationGitCopy(string language)
    {
        var path = Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", $"Home.{language}.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        Assert.True(json.RootElement.TryGetProperty("CreateGitRepository", out _));
        Assert.True(json.RootElement.TryGetProperty("CreateGitRepositoryHint", out _));
    }
}
