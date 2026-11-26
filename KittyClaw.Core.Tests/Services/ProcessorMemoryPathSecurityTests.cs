using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class ProcessorMemoryPathSecurityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_processor_memory_is_not_read_or_modified(bool legacy)
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var projects = new ProjectService(root.Path);
        var project = await projects.CreateProjectAsync("Processor memory path fixture");
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var columns = new ColumnService(projects, processors);
        var column = (await columns.ListColumnsAsync(project.Slug)).First();
        await processors.SaveAsync(project.Slug, column.Id, "Worker", "Ordinary mission.", null, true, 20, [], [], []);
        var workspace = projects.ResolveWorkspacePath(project);
        var canonical = Path.Combine(workspace, ".agents", "processors", $"column-{column.Id}", "memory", "MEMORY.md");
        var before = await File.ReadAllTextAsync(canonical);
        var link = legacy ? Path.Combine(workspace, ".agents", $"column-{column.Id}") : Path.GetDirectoryName(canonical)!;
        if (!legacy) Directory.Delete(link, true);
        var sentinel = Path.Combine(outside.Path, legacy ? "memory.md" : "MEMORY.md");
        await File.WriteAllTextAsync(sentinel, "- Synthetic external lesson.\n");
        if (OperatingSystem.IsWindows()) DurableWriteRouter.CreateDirectoryJunction(link, outside.Path);
        else Directory.CreateSymbolicLink(link, outside.Path);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => processors.GetMemoryIndexPathAsync(project.Slug, column.Id));
            Assert.Equal("- Synthetic external lesson.\n", await File.ReadAllTextAsync(sentinel));
            if (legacy) Assert.Equal(before, await File.ReadAllTextAsync(canonical));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Capitalization_cannot_rewrite_memory_through_a_linked_directory()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        var projects = new ProjectService(root.Path);
        var project = await projects.CreateProjectAsync("Capitalization path fixture");
        var link = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "processors", "column-4", "memory");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var sentinel = Path.Combine(outside.Path, "MEMORY.md");
        await File.WriteAllTextAsync(sentinel, "Synthetic external memory.");
        if (OperatingSystem.IsWindows()) DurableWriteRouter.CreateDirectoryJunction(link, outside.Path);
        else Directory.CreateSymbolicLink(link, outside.Path);
        try
        {
            var result = await new ColumnMemoryCapitalizationService(projects).CapitalizeAsync(
                project.Slug, 4, "fixture", ["A synthetic lesson long enough to be persisted."]);
            Assert.Equal(MemoryCapitalizationStatus.Failed, result.Status);
            Assert.Contains("linked", result.Error!);
            Assert.Equal("Synthetic external memory.", await File.ReadAllTextAsync(sentinel));
            Assert.Single(Directory.GetFiles(outside.Path));
        }
        finally { Directory.Delete(link); }
    }
}
