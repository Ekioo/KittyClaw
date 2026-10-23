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
            Assert.Equal(30, isolated.Workloads.Count);
            Assert.Equal(30, isolated.Workloads.Select(w => w.Name).Distinct().Count());
            var projects = new ProjectService(directory);
            Assert.Equal(4, (await projects.ListProjectsAsync()).Count);
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
                var concurrent = isolated.Workloads.Single(w => w.Name == $"sqlite.concurrent/{size}/4r-2w");
                var tokensBefore = await db.Tickets.OrderBy(t => t.Id).Take(2).Select(t => t.AgentTokens).ToArrayAsync();
                await concurrent.Operation();
                var tokensAfter = await db.Tickets.OrderBy(t => t.Id).Take(2).Select(t => t.AgentTokens).ToArrayAsync();
                Assert.Equal(tokensBefore.Select(t => t + 4), tokensAfter);
                Assert.Equal(size, await db.Tickets.CountAsync());
            }
            foreach (var (slug, size) in isolated.ComplexProjectSlugs.Zip(new[] { 100, 1000 }))
            {
                await using var db = projects.GetProjectDb(slug);
                Assert.Equal(size, await db.Tickets.CountAsync());
                Assert.Equal(size * 4 / 5, await db.Tickets.CountAsync(t => t.ParentId != null));
                Assert.Equal(size / 5 - 1, await db.TicketDependencies.CountAsync());
                Assert.Equal(size * 5, await db.Comments.CountAsync());
                Assert.Equal(size * 3, await db.ActivityEntries.CountAsync());
                Assert.Equal(12, await db.Labels.CountAsync());
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
