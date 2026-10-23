using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Http;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProcessorAcceptanceOptionTests
{
    [Fact]
    public async Task Option_defaults_off_roundtrips_files_and_preserves_omitted_values()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Option");
        var columns = new ColumnService(projects);
        var source = (await columns.ListColumnsAsync(project.Slug)).First();
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        async Task<ColumnProcessor> Save(bool? value) => await processors.SaveAsync(project.Slug, source.Id, "Worker", "Work", null, false, 20, [], [], [], evaluateAcceptanceCriteria: value);
        Assert.False((await Save(null)).EvaluateAcceptanceCriteria);
        var handler = await Endpoints.HandleSaveColumnProcessor(project.Slug, source.Id,
            new SaveColumnProcessorRequest("Worker", "Work", Enabled: false, EvaluateAcceptanceCriteria: true), processors, new BoardUpdateNotifier());
        Assert.Equal(200, ((IStatusCodeHttpResult)handler).StatusCode);
        Assert.True((await processors.GetAsync(project.Slug, source.Id))!.EvaluateAcceptanceCriteria);
        Assert.True((await Save(null)).EvaluateAcceptanceCriteria);
        var freshService = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        Assert.True((await freshService.GetAsync(project.Slug, source.Id))!.EvaluateAcceptanceCriteria);
        Assert.False((await Save(false)).EvaluateAcceptanceCriteria);
    }
}
