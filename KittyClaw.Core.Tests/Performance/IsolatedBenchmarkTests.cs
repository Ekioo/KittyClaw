using KittyClaw.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Performance;

public sealed class IsolatedBenchmarkTests
{
    [Fact]
    public async Task IoWorkloadsUseSyntheticDatabasesAndRestoreEnvironmentOnDisposal()
    {
        var variables = new[] { "KITTYCLAW_DATA_DIR", "ASPNETCORE_URLS", "KITTYCLAW_DESKTOP" };
        var before = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        string directory;
        using (var isolated = new IsolatedBenchmarks())
        {
            directory = isolated.DataDirectory;
            await isolated.InitializeAsync();
            Assert.Equal(directory, Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR"));
            Assert.Equal(18, isolated.Workloads.Count);
            Assert.Equal(18, isolated.Workloads.Select(w => w.Name).Distinct().Count());
            var projects = new ProjectService(directory);
            Assert.Equal(2, (await projects.ListProjectsAsync()).Count);
            foreach (var (slug, size) in isolated.ProjectSlugs.Zip(new[] { 100, 1000 }))
            {
                await using var db = projects.GetProjectDb(slug);
                Assert.Equal(size, await db.Tickets.CountAsync());
                Assert.Equal(size, await db.Comments.CountAsync());
                var write = isolated.Workloads.Single(w => w.Name == $"sqlite.update/{size}");
                var first = await db.Tickets.AsNoTracking().OrderBy(t => t.Id).FirstAsync();
                await write.Operation();
                var second = await db.Tickets.AsNoTracking().OrderBy(t => t.Id).FirstAsync();
                Assert.NotEqual(first.Description, second.Description);
                Assert.Equal(size, await db.Tickets.CountAsync());
            }
        }
        Assert.False(Directory.Exists(directory));
        foreach (var name in variables) Assert.Equal(before[name], Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public async Task AsyncMeasurementCountsAllocationsAcrossThreadSwitches()
    {
        object? result = null;
        var measurement = await BenchmarkProgram.MeasureAsync("async-allocation", async () =>
        {
            await Task.Yield();
            result = new byte[1024];
        });
        Assert.Equal("process", measurement.AllocationScope);
        Assert.True(measurement.BytesPerOperation >= 1024);
        Assert.True(measurement.MedianNanoseconds > 0);
        Assert.Equal(9, measurement.Samples.Length);
        GC.KeepAlive(result);
    }
}
