using System.Text.Json.Nodes;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Http;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProcessorFallbackOptionTests
{
    [Fact]
    public async Task Fallback_option_roundtrips_api_and_authoritative_files_and_preserves_legacy_defaults()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Fallback option");
        var source = (await new ColumnService(projects).ListColumnsAsync(project.Slug)).First();
        var skills = new ProjectSkillService(projects);
        var processors = new ColumnProcessorService(projects, skills);
        Task<ColumnProcessor> Save(bool? value) => processors.SaveAsync(project.Slug, source.Id,
            "Worker", "Work", null, false, 20, [], [], [], jevFallbackToAgent: value);
        Assert.True((await Save(null)).JevFallbackToAgent);
        var result = await Endpoints.HandleSaveColumnProcessor(project.Slug, source.Id,
            new SaveColumnProcessorRequest("Worker", "Work", Enabled: false, JevFallbackToAgent: false),
            processors, new BoardUpdateNotifier());
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False((await Save(null)).JevFallbackToAgent);
        Assert.False((await new ColumnProcessorService(projects, skills).GetAsync(project.Slug, source.Id))!.JevFallbackToAgent);
        var path = await processors.GetDefinitionPathAsync(project.Slug, source.Id);
        var definition = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.False(definition["jevFallbackToAgent"]!.GetValue<bool>());
        definition.Remove("jevFallbackToAgent");
        await File.WriteAllTextAsync(path, definition.ToJsonString());
        Assert.True((await processors.GetAsync(project.Slug, source.Id))!.JevFallbackToAgent);
    }
}
