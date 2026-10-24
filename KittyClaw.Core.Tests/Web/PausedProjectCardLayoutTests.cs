using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Web;

public sealed class PausedProjectCardLayoutTests
{
    private static string Source => File.ReadAllText(Path.Combine(
        AppCssHelper.FindRepoRoot(), "KittyClaw.Web", "Components", "ProjectCards.razor"));

    [Fact]
    public void ProjectDirectory_HasAllThreeDirectDestinations()
    {
        Assert.Contains("href=\"/board/@project.Slug\"", Source);
        Assert.Contains("href=\"/board/@project.Slug/dashboard\"", Source);
        Assert.Contains("href=\"/board/@project.Slug/settings\"", Source);
    }

    [Fact]
    public void ProjectDirectory_KeepsPauseStatusAndIntegrationWarningsAsTags()
    {
        Assert.Contains("project-paused-badge", Source);
        Assert.Contains("project-card-integration-alert", Source);
        Assert.Contains("ProjectIntegrationBlockedByLocalChanges", Source);
        Assert.Contains("GetAlertSummaryAsync", Source);
        Assert.DoesNotContain("project-card-owner-actions", Source);
    }
}
