using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ColumnMemoryCapitalizationServiceTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public async Task Persists_lesson_and_makes_it_available_in_the_canonical_index()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Memory persistence");
        var service = new ColumnMemoryCapitalizationService(projects);

        var result = await service.CapitalizeAsync(project.Slug, 12, "run-1",
            ["Prefer stable column identifiers when display names can change."]);

        Assert.Equal(MemoryCapitalizationStatus.Succeeded, result.Status);
        var index = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors",
            "column-12", "memory", "MEMORY.md");
        Assert.Contains("stable column identifiers", await File.ReadAllTextAsync(index));
    }

    [Fact]
    public async Task Empty_lessons_are_an_explicit_no_change_without_filesystem_noise()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("No memory change");
        var service = new ColumnMemoryCapitalizationService(projects);

        var result = await service.CapitalizeAsync(project.Slug, 7, "run-empty", []);

        Assert.Equal(MemoryCapitalizationStatus.NoChange, result.Status);
        Assert.False(Directory.Exists(Path.Combine(projects.ResolveWorkspacePath(project),
            ".agents", "processors", "column-7", "memory")));
    }

    [Fact]
    public async Task Replaying_checkpoint_and_rephrased_duplicate_does_not_duplicate_lessons()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Memory replay");
        var service = new ColumnMemoryCapitalizationService(projects);
        const string lesson = "Persist the business result before starting recoverable post-processing.";

        await service.CapitalizeAsync(project.Slug, 4, "run-1", [lesson]);
        var replay = await service.CapitalizeAsync(project.Slug, 4, "run-1", [lesson]);
        var duplicate = await service.CapitalizeAsync(project.Slug, 4, "run-2",
            ["  Persist   the business result before starting recoverable post-processing. "]);

        Assert.Equal(MemoryCapitalizationStatus.Succeeded, replay.Status);
        Assert.Equal(MemoryCapitalizationStatus.NoChange, duplicate.Status);
        var topic = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors",
            "column-4", "memory", "pipeline-lessons.md");
        Assert.Equal(1, (await File.ReadAllTextAsync(topic)).Split("- Persist").Length - 1);
    }

    [Fact]
    public async Task Replay_repairs_index_when_first_attempt_stops_between_topic_and_index_writes()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Interrupted memory commit");
        var service = new FailFirstIndexWriteService(projects);
        const string lesson = "Rebuild every derived memory index from the durable lesson journal after interruption.";

        var interrupted = await service.CapitalizeAsync(project.Slug, 6, "run-interrupted", [lesson]);

        Assert.Equal(MemoryCapitalizationStatus.Failed, interrupted.Status);
        var memory = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors",
            "column-6", "memory");
        Assert.Contains("checkpoint:run-interrupted",
            await File.ReadAllTextAsync(Path.Combine(memory, "pipeline-lessons.md")));
        Assert.False(File.Exists(Path.Combine(memory, "MEMORY.md")));

        var resumed = await service.CapitalizeAsync(project.Slug, 6, "run-interrupted", [lesson]);

        Assert.Equal(MemoryCapitalizationStatus.Succeeded, resumed.Status);
        Assert.Equal(0, resumed.Added);
        Assert.Contains(lesson, await File.ReadAllTextAsync(Path.Combine(memory, "MEMORY.md")));
        Assert.Equal(1, (await File.ReadAllTextAsync(Path.Combine(memory, "pipeline-lessons.md")))
            .Split("checkpoint:run-interrupted").Length - 1);
    }

    [Fact]
    public async Task Memory_is_bounded_deterministically_to_the_newest_lessons()
    {
        var projects = new ProjectService(_temp.Path);
        var project = await projects.CreateProjectAsync("Bounded memory");
        var service = new ColumnMemoryCapitalizationService(projects);
        for (var i = 0; i < ColumnMemoryCapitalizationService.MaximumLessons + 3; i++)
            await service.CapitalizeAsync(project.Slug, 9, $"run-{i}",
                [$"Reusable lesson number {i:D2} with enough concrete detail."]);

        var topic = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors",
            "column-9", "memory", "pipeline-lessons.md");
        var content = await File.ReadAllTextAsync(topic);
        Assert.DoesNotContain("lesson number 00", content);
        Assert.Contains("lesson number 52", content);
        Assert.Equal(ColumnMemoryCapitalizationService.MaximumLessons,
            content.Split("<!-- checkpoint:").Length - 1);
    }

    public void Dispose() => _temp.Dispose();

    private sealed class FailFirstIndexWriteService(ProjectService projects)
        : ColumnMemoryCapitalizationService(projects)
    {
        private bool _failed;

        protected override Task WriteAtomicallyAsync(
            string path, string content, CancellationToken cancellationToken)
        {
            if (!_failed && string.Equals(Path.GetFileName(path), "MEMORY.md", StringComparison.Ordinal))
            {
                _failed = true;
                throw new IOException("Injected failure between topic and index replacement.");
            }
            return base.WriteAtomicallyAsync(path, content, cancellationToken);
        }
    }
}
