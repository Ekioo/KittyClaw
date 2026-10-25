using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public class TemporaryStorageServiceTests
{
    [Fact]
    public void Measure_CountsNestedFilesAndDoesNotCreateMissingFolders()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "nested"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "first"), new byte[100]);
        File.WriteAllBytes(Path.Combine(tmp.Path, "nested", "second"), new byte[250]);
        var usage = TemporaryStorageService.Measure(tmp.Path);
        Assert.Equal(350, usage.Bytes);
        Assert.Equal(2, usage.FileCount);
        Assert.True(usage.Exists);
        Assert.False(usage.IsPartial);
        var missing = Path.Combine(tmp.Path, "missing");
        var absent = TemporaryStorageService.Measure(missing);
        Assert.False(absent.Exists);
        Assert.False(absent.IsPartial);
        Assert.Equal(0, absent.Bytes);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Scan_IncludesHistoricalRootsAndDeduplicatesNestedRoots()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("Storage project");
        var root = projects.ResolveWorkingFilesRoot(project);
        var nested = Path.Combine(root, "run-one");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "current"), new byte[200]);
        // A configured nested root must not double count the containing default root.
        await projects.UpdateProjectAsync(project.Slug, null, workingFilesPath: nested);
        var oldRunFolder = Path.Combine(tmp.Path, "previous", "run-old");
        Directory.CreateDirectory(oldRunFolder);
        File.WriteAllBytes(Path.Combine(oldRunFolder, "old-evidence"), new byte[300]);
        var runs = new AgentRunRegistry();
        runs.Register(new AgentRun
        {
            RunId = "run-old", ProjectSlug = project.Slug, AgentName = "agent", SkillFile = "inline",
            ConcurrencyGroup = "agent", TicketId = null, StartedAt = DateTime.UtcNow,
            WorkingFilesPath = oldRunFolder, Status = AgentRunStatus.Completed
        });
        var usage = Assert.Single(await new TemporaryStorageService(projects, runs).ScanAsync());
        Assert.Equal(500, usage.Bytes);
        Assert.Equal(2, usage.FileCount);
        Assert.Equal(2, usage.Folders.Count);
        Assert.False(usage.IsPartial);
        Assert.True(File.Exists(Path.Combine(oldRunFolder, "old-evidence")));
    }

    [Fact]
    public async Task Scan_SortsProjectsBySizeAndLeavesMissingRootsUncreated()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var empty = await projects.CreateProjectAsync("Empty");
        var large = await projects.CreateProjectAsync("Large");
        var root = projects.PrepareRunWorkingFilesDirectory(large, "run-one");
        File.WriteAllBytes(Path.Combine(root, "report"), new byte[400]);
        var usage = await new TemporaryStorageService(projects, new AgentRunRegistry()).ScanAsync();
        Assert.Equal(large.Slug, usage[0].Slug);
        Assert.Equal(400, usage[0].Bytes);
        Assert.Equal(0, usage[1].Bytes);
        Assert.False(Directory.Exists(projects.ResolveWorkingFilesRoot(empty)));
    }

    [Fact]
    public void Measure_CanBeCancelledAndMarksInvalidDirectoryAsPartial()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "not-a-directory");
        File.WriteAllText(file, "test");
        Assert.True(TemporaryStorageService.Measure(file).IsPartial);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => TemporaryStorageService.Measure(tmp.Path, cts.Token));
    }
}
