using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProjectSkillAndProcessorTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ProjectService _projects;
    private readonly ProjectSkillService _skills;
    private readonly ColumnProcessorService _processors;
    private readonly ColumnService _columns;
    private readonly PipelineService _pipelines;

    public ProjectSkillAndProcessorTests()
    {
        _projects = new ProjectService(_temp.Path);
        _skills = new ProjectSkillService(_projects);
        _processors = new ColumnProcessorService(_projects, _skills);
        _columns = new ColumnService(_projects);
        _pipelines = new PipelineService(_projects);
    }

    [Fact]
    public async Task Project_skill_keeps_stable_slug_when_display_name_changes()
    {
        var project = await _projects.CreateProjectAsync("Skills");
        var skill = await _skills.CreateAsync(project.Slug, "Run tests", "# Run tests\n");

        var renamed = await _skills.UpdateAsync(project.Slug, skill.Slug, "Verify build", null);

        Assert.NotNull(renamed);
        Assert.Equal("run-tests", renamed.Slug);
        Assert.Equal("Verify build", renamed.Name);
        Assert.Equal("# Run tests\n", await _skills.ReadInstructionsAsync(project.Slug, skill.Slug));
    }

    [Fact]
    public async Task Processor_is_bound_to_stable_column_and_owns_memory()
    {
        var project = await _projects.CreateProjectAsync("Processors");
        var pipeline = await _pipelines.CreateAsync(project.Slug, "Editorial");
        var column = await _columns.CreateColumnAsync(project.Slug, "Review", pipelineId: pipeline.Id);
        var factCheck = await _skills.CreateAsync(project.Slug, "Fact check", "Verify factual claims.");

        var saved = await _processors.SaveAsync(
            project.Slug, column.Id, "Editorial reviewer", "Validate publication safety.", null,
            enabled: true, maxTurns: 50, availableSkills: [],
            recommendedSkills: [factCheck.Slug], requiredSkills: [factCheck.Slug]);
        await _pipelines.UpdateAsync(project.Slug, pipeline.Id, "Content");
        await _columns.UpdateColumnAsync(project.Slug, column.Id, name: "Validation");

        var reloaded = await _processors.GetAsync(project.Slug, column.Id);
        var memoryPath = await _processors.GetMemoryIndexPathAsync(project.Slug, column.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(saved.Id, reloaded.Id);
        Assert.Equal(column.Id, reloaded.ColumnId);
        Assert.Equal([factCheck.Slug], reloaded.RequiredSkills);
        Assert.NotNull(memoryPath);
        Assert.True(File.Exists(memoryPath));
        Assert.Contains($"column-{column.Id}", memoryPath);
    }

    [Fact]
    public async Task Processor_rejects_unknown_project_skills()
    {
        var project = await _projects.CreateProjectAsync("Unknown skill");
        var column = (await _columns.ListColumnsAsync(project.Slug)).First();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _processors.SaveAsync(
            project.Slug, column.Id, "Worker", "Do the work.", null, true, 20,
            availableSkills: ["missing"], recommendedSkills: [], requiredSkills: []));

        Assert.Contains("missing", error.Message);
    }

    public void Dispose() => _temp.Dispose();
}
