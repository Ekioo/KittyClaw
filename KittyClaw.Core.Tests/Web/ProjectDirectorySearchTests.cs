using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public sealed class ProjectDirectorySearchTests
{
    private readonly Project _project = new() { Name = "Mobile UX Demo", Slug = "mobile-ux-demo" };
    private readonly BoardColumnSummary[] _columns = [
        new(1, 1, "Main", "Review", "#a78bfa", 2),
        new(2, 2, "Release", "Published", "#3ecf8e", 0)];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MOBILE")]
    [InlineData("mobile-ux")]
    [InlineData("review")]
    [InlineData("release")]
    [InlineData("mobile published")]
    [InlineData("  mobile\tReview  ")]
    public void FindsProjectsByNameSlugColumnOrPipeline(string? query)
    {
        Assert.True(ProjectDirectorySearch.Matches(_project, _columns, query));
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("mobile missing")]
    [InlineData("#123")]
    public void UnmatchedTermsDoNotLeakUnrelatedProjects(string query)
    {
        Assert.False(ProjectDirectorySearch.Matches(_project, _columns, query));
    }

    [Fact]
    public void ProjectsWithoutColumnsCanStillBeFoundByName()
    {
        Assert.True(ProjectDirectorySearch.Matches(_project, [], "demo"));
        Assert.False(ProjectDirectorySearch.Matches(_project, [], "review"));
    }
}
