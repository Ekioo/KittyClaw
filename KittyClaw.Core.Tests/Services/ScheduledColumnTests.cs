using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

/// <summary>
/// Tests that the "Scheduled" board column (feature #99) is seeded on new boards and
/// idempotently back-filled on boards that predate the feature.
/// </summary>
public sealed class ScheduledColumnTests
{
    private static (ColumnService columns, string slug) BuildSut(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("scheduled-col-test").GetAwaiter().GetResult();
        var columns = new ColumnService(projects);
        return (columns, project.Slug);
    }

    [Fact]
    public async Task NewBoard_HasScheduledColumn_RightAfterBlocked()
    {
        using var tmp = new TempDir();
        var (columns, slug) = BuildSut(tmp);

        var list = await columns.ListColumnsAsync(slug);
        var names = list.Select(c => c.Name).ToList();

        Assert.Contains("Scheduled", names);
        Assert.Equal(names.IndexOf("Blocked") + 1, names.IndexOf("Scheduled"));
    }

    [Fact]
    public async Task LegacyBoard_MissingScheduled_GetsItBackFilledOnce()
    {
        using var tmp = new TempDir();
        var (columns, slug) = BuildSut(tmp);

        // Simulate a pre-feature board: remove the Scheduled column.
        var scheduled = (await columns.ListColumnsAsync(slug)).First(c => c.Name == "Scheduled");
        Assert.True(await columns.DeleteColumnAsync(slug, scheduled.Id, "Todo"));

        // Next read must back-fill it idempotently.
        var afterFirst = (await columns.ListColumnsAsync(slug)).Select(c => c.Name).ToList();
        var afterSecond = (await columns.ListColumnsAsync(slug)).Select(c => c.Name).ToList();

        Assert.Single(afterFirst, n => n == "Scheduled");
        Assert.Single(afterSecond, n => n == "Scheduled");
    }

    [Fact]
    public async Task CustomizedPipeline_DeletedScheduledColumn_IsNotRecreated()
    {
        using var tmp = new TempDir();
        var (columns, slug) = BuildSut(tmp);

        var initial = await columns.ListColumnsAsync(slug);
        var backlog = initial.First(c => c.Name == "Backlog");
        var scheduled = initial.First(c => c.Name == "Scheduled");
        Assert.NotNull(await columns.UpdateColumnAsync(slug, backlog.Id, name: "À qualifier"));
        Assert.True(await columns.DeleteColumnAsync(slug, scheduled.Id, "Todo"));

        var firstRead = await columns.ListColumnsAsync(slug);
        var secondRead = await columns.ListColumnsAsync(slug);

        Assert.DoesNotContain(firstRead, c => c.Name == "Scheduled");
        Assert.DoesNotContain(secondRead, c => c.Name == "Scheduled");
    }
}
