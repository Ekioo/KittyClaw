using KittyClaw.Core.Services;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KittyClaw.Core.Tests;

public sealed class ProjectUpdateReviewTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "kittyclaw-update-tests-" + Guid.NewGuid());

    [Fact]
    public async Task BaselinesThenQueuesMissedReleasesExactlyOnce()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Update test");
        var service = new ProjectUpdateReviewService(projects);
        Assert.False(await service.CheckAsync(project.Slug, "## [v0.24]\nOld feature"));
        const string notes = "## [Unreleased]\nFuture feature\n## [v0.26]\nNew feature\n## [v0.25]\nIntermediate feature\n## [v0.24]\nOld feature";
        Assert.True(await service.CheckAsync(project.Slug, notes));
        Assert.False(await new ProjectUpdateReviewService(projects).CheckAsync(project.Slug, notes));
        Assert.False(await service.CheckAsync(project.Slug, "## [v0.25]\nDowngrade"));
        await using var db = projects.GetProjectDb(project.Slug);
        var ticket = await db.Tickets.SingleAsync(t => t.CreatedBy == "kittyclaw-update");
        Assert.Contains("New feature", ticket.Description);
        Assert.Contains("Intermediate feature", ticket.Description);
        Assert.DoesNotContain("Old feature", ticket.Description);
        Assert.DoesNotContain("Future feature", ticket.Description);
        Assert.Contains("do not implement changes", ticket.Description);
        Assert.Contains("owner approval", ticket.Description);
    }

    [Fact]
    public async Task ConcurrentScansDoNotDuplicateReview()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Concurrent update");
        var service = new ProjectUpdateReviewService(projects);
        await service.CheckAsync(project.Slug, "## [v1.0]\nBaseline");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            new ProjectUpdateReviewService(projects).CheckAsync(project.Slug, "## [v1.1]\nNew")));
        Assert.Single(results, created => created);
    }

    [Fact]
    public void ReleaseNotesShipWithApplication() =>
        Assert.Contains("## [v", ProjectUpdateReviewService.ReadBundledNotes());

    [Fact]
    public async Task ManualReviewWorksBeforeFirstScanAndCanRepeatOnSameVersion()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Manual review");
        var pipeline = await new PipelineService(projects).CreateAsync(project.Slug, "Analysis");
        var column = await new ColumnService(projects).CreateColumnAsync(project.Slug, "Analyze", pipelineId: pipeline.Id);
        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumnId: column.Id);
        var service = new ProjectUpdateReviewService(projects);
        const string notes = "## [Unreleased]\nFuture\n## [v1.1]\nLatest feature\n## [v1.0]\nOld feature";
        var first = await service.RequestManualAsync(project.Slug, notes);
        var second = await service.RequestManualAsync(project.Slug, notes);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(column.Id, first.ColumnId);
        Assert.Equal(pipeline.Id, first.PipelineId);
        Assert.Contains("manual review", first.Description);
        Assert.Contains("Latest feature", first.Description);
        Assert.DoesNotContain("Old feature", first.Description);
        Assert.DoesNotContain("Future", first.Description);
        Assert.False(await service.CheckAsync(project.Slug, notes));
    }

    [Fact]
    public async Task ManualReviewDoesNotConsumePendingAutomaticReview()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Pending update");
        var service = new ProjectUpdateReviewService(projects);
        await service.CheckAsync(project.Slug, "## [v1.0]\nBaseline");
        const string notes = "## [v1.2]\nNewest\n## [v1.1]\nMissed\n## [v1.0]\nBaseline";
        var manual = await service.RequestManualAsync(project.Slug, notes);
        Assert.Contains("Missed", manual.Description);
        Assert.True(await service.CheckAsync(project.Slug, notes));
        Assert.False(await service.CheckAsync(project.Slug, notes));
    }

    [Fact]
    public async Task ManualReviewReportsUnavailableTargetAndCanRetry()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Missing manual target");
        var column = await new ColumnService(projects).CreateColumnAsync(project.Slug, "Analyze");
        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumnId: column.Id);
        await using (var db = projects.GetProjectDb(project.Slug))
            await db.BoardColumns.Where(c => c.Id == column.Id).ExecuteDeleteAsync();
        var service = new ProjectUpdateReviewService(projects);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestManualAsync(project.Slug, "## [v1.0]\nNotes"));
        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumn: true);
        Assert.NotNull(await service.RequestManualAsync(project.Slug, "## [v1.0]\nNotes"));
    }

    [Fact]
    public async Task ConfiguredColumnUsesItsPipelineAndSurvivesUnrelatedUpdates()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Configured target");
        var pipeline = await new PipelineService(projects).CreateAsync(project.Slug, "Analysis");
        var columns = new ColumnService(projects);
        var target = await columns.CreateColumnAsync(project.Slug, "Release analysis", pipelineId: pipeline.Id,
            requireAcceptanceCriteria: true);
        await projects.UpdateProjectAsync(project.Slug, directory);
        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumnId: target.Id);
        var saved = await new ProjectService(directory).GetProjectAsync(project.Slug);
        Assert.Equal(target.Id, saved!.UpdateReviewColumnId);
        Assert.Equal(directory, saved.WorkspacePath);
        await projects.UpdateProjectAsync(project.Slug, directory, fallbackModel: "test", updateFallback: true);
        Assert.Equal(target.Id, (await projects.GetProjectAsync(project.Slug))!.UpdateReviewColumnId);

        var service = new ProjectUpdateReviewService(projects);
        await service.CheckAsync(project.Slug, "## [v1.0]\nBaseline");
        Assert.True(await service.CheckAsync(project.Slug, "## [v1.1]\nNew"));
        await using var db = projects.GetProjectDb(project.Slug);
        var ticket = await db.Tickets.SingleAsync(t => t.CreatedBy == "kittyclaw-update");
        Assert.Equal(target.Id, ticket.ColumnId);
        Assert.Equal(pipeline.Id, ticket.PipelineId);
        Assert.NotEmpty(ticket.AcceptanceCriteria);

        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumn: true);
        Assert.Null((await projects.GetProjectAsync(project.Slug))!.UpdateReviewColumnId);
        Assert.Equal(directory, (await projects.GetProjectAsync(project.Slug))!.WorkspacePath);
    }

    [Fact]
    public async Task MissingTargetWaitsForCorrectionWithoutAdvancingCheckpoint()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Deleted target");
        var columns = new ColumnService(projects);
        var target = await columns.CreateColumnAsync(project.Slug, "Release analysis");
        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumnId: target.Id);
        var service = new ProjectUpdateReviewService(projects);
        await service.CheckAsync(project.Slug, "## [v1.0]\nBaseline");
        await using (var db = projects.GetProjectDb(project.Slug))
            await db.BoardColumns.Where(c => c.Id == target.Id).ExecuteDeleteAsync();
        Assert.False(await service.CheckAsync(project.Slug, "## [v1.1]\nNew"));
        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumn: true);
        Assert.True(await service.CheckAsync(project.Slug, "## [v1.1]\nNew"));
    }

    [Fact]
    public async Task RejectsInvalidAndIncompatibleTargets()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Invalid target");
        var columns = new ColumnService(projects);
        var terminal = await columns.CreateColumnAsync(project.Slug, "Complete", role: ColumnRole.Success);
        var gated = await columns.CreateColumnAsync(project.Slug, "Validated", requireValidatedAcceptanceCriteria: true);
        foreach (var id in new[] { int.MaxValue, terminal.Id, gated.Id })
            await Assert.ThrowsAsync<InvalidOperationException>(() => projects.UpdateProjectAsync(
                project.Slug, null, updateReviewColumnId: id));
    }

    [Fact]
    public async Task RenamedOwnerActionTargetRetainsIdentityAndOwnerAssignment()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Owner target");
        var column = await new ColumnService(projects).CreateColumnAsync(project.Slug, "Decisions", role: ColumnRole.OwnerAction);
        await projects.UpdateProjectAsync(project.Slug, null, updateReviewColumnId: column.Id);
        var service = new ProjectUpdateReviewService(projects);
        await service.CheckAsync(project.Slug, "## [v1.0]\nBaseline");
        await using (var db = projects.GetProjectDb(project.Slug))
            await db.BoardColumns.Where(c => c.Id == column.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Owner review"));
        Assert.True(await service.CheckAsync(project.Slug, "## [v1.1]\nNew"));
        await using var read = projects.GetProjectDb(project.Slug);
        var ticket = await read.Tickets.SingleAsync(t => t.CreatedBy == "kittyclaw-update");
        Assert.Equal(column.Id, ticket.ColumnId);
        Assert.Equal("Owner review", ticket.Status);
        Assert.Equal("owner", ticket.AssignedTo);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
