using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class CostReportServiceTests
{
    [Fact]
    public async Task ConcurrentIdenticalLoads_AreSerializedAndReuseTheSameResult()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Concurrent costs");
        var tickets = new TicketService(projects, new MemberService(projects));
        var service = new CostReportService(projects, new PipelineService(projects), tickets);
        var day = DateOnly.FromDateTime(DateTime.Now);
        Write(projects.ResolveWorkspacePath(project), "cost-log.jsonl",
            Entry(day, 3m, project.Slug, pipeline: 1, run: "concurrent"));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.RefreshAsync()));
        var optionLoads = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => service.GetOptionsAsync()));
        Assert.All(optionLoads, result => Assert.Same(optionLoads[0], result));

        var filter = new CostReportFilter(day, day);
        var reportLoads = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => service.GetReportAsync(filter)));
        Assert.All(reportLoads, result => Assert.Equal(reportLoads[0].TotalUsd, result.TotalUsd));
        Assert.Equal(3m, reportLoads[0].TotalUsd);
    }

    [Fact]
    public async Task AggregatesCurrentAndArchive_Inclusive_SkipsMalformedAndDuplicateRuns()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var alpha = await projects.CreateProjectAsync("Alpha");
        var beta = await projects.CreateProjectAsync("Beta");
        var tickets = new TicketService(projects, new MemberService(projects));
        var service = new CostReportService(projects, new PipelineService(projects), tickets);
        var day = DateOnly.FromDateTime(DateTime.Now);

        Write(projects.ResolveWorkspacePath(alpha), "cost-log-2026-08.jsonl",
            Entry(day, 1.25m, alpha.Slug, pipeline: 1, estimated: true, run: "a"),
            "{not-json");
        Write(projects.ResolveWorkspacePath(alpha), "cost-log.jsonl",
            Entry(day, 1.25m, alpha.Slug, pipeline: 1, estimated: true, run: "a"));
        Write(projects.ResolveWorkspacePath(beta), "cost-log.jsonl",
            Entry(day, 2.75m, beta.Slug, pipeline: null, estimated: false, run: "b"));

        await service.RefreshAsync();

        var report = await service.GetReportAsync(new(day, day));

        Assert.Equal(4m, report.TotalUsd);
        Assert.True(report.Estimated);
        Assert.Equal(2, report.Projects.Count);
        Assert.Equal(2, report.Daily.Count);
    }

    [Fact]
    public async Task PipelineFilter_UsesSnapshot_FallsBackToCurrentTicket_AndExcludesTicketless()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Pipeline project");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Old record");
        var service = new CostReportService(projects, new PipelineService(projects), tickets);
        var day = DateOnly.FromDateTime(DateTime.Now);
        Write(projects.ResolveWorkspacePath(project), "cost-log.jsonl",
            Entry(day, 1m, project.Slug, pipeline: ticket.PipelineId, run: "snapshot"),
            Entry(day, 2m, project.Slug, pipeline: null, ticketId: ticket.Id, run: "legacy"),
            Entry(day, 4m, project.Slug, pipeline: null, ticketId: null, run: "ticketless"));

        await service.RefreshAsync();

        var all = await service.GetReportAsync(new(day, day));
        var filtered = await service.GetReportAsync(new(day, day, PipelineKey: $"{project.Slug}:{ticket.PipelineId}"));

        Assert.Equal(7m, all.TotalUsd);
        Assert.Equal(3m, filtered.TotalUsd);
    }

    [Fact]
    public async Task UnknownPipeline_FilterIncludesOrphanedLegacyTicket_ButExcludesTicketlessRun()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Unknown pipeline project");
        var tickets = new TicketService(projects, new MemberService(projects));
        var service = new CostReportService(projects, new PipelineService(projects), tickets);
        var day = DateOnly.FromDateTime(DateTime.Now);
        Write(projects.ResolveWorkspacePath(project), "cost-log.jsonl",
            Entry(day, 2m, project.Slug, pipeline: null, ticketId: 999_999, run: "orphan"),
            Entry(day, 4m, project.Slug, pipeline: null, ticketId: null, run: "ticketless"));

        await service.RefreshAsync();

        var options = await service.GetOptionsAsync();
        var filtered = await service.GetReportAsync(new(day, day, PipelineKey: $"{project.Slug}:unknown"));

        Assert.Contains(options.Pipelines, option => option.Key == $"{project.Slug}:unknown");
        Assert.Equal(2m, filtered.TotalUsd);
    }

    [Fact]
    public async Task RepeatedLegacyEntries_ResolveTicketPipelineInOneSetBasedLookup()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Legacy costs");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Legacy ticket");
        var service = new CostReportService(projects, new PipelineService(projects), tickets);
        var day = DateOnly.FromDateTime(DateTime.Now);
        Write(projects.ResolveWorkspacePath(project), "cost-log.jsonl",
            Enumerable.Range(0, 500)
                .Select(index => Entry(day, 1m, project.Slug, pipeline: null,
                    ticketId: ticket.Id, run: $"legacy-{index}"))
                .ToArray());

        await service.RefreshAsync();

        var report = await service.GetReportAsync(
            new(day, day, PipelineKey: $"{project.Slug}:{ticket.PipelineId}"));

        Assert.Equal(500m, report.TotalUsd);
    }

    [Fact]
    public async Task DateRange_UsesInstanceLocalDayAtUtcBoundary()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Local day project");
        var tickets = new TicketService(projects, new MemberService(projects));
        var service = new CostReportService(projects, new PipelineService(projects), tickets);
        var localDay = new DateOnly(2026, 8, 11);
        var localBoundary = localDay.ToDateTime(new TimeOnly(0, 30), DateTimeKind.Unspecified);
        var utcInstant = TimeZoneInfo.ConvertTimeToUtc(localBoundary, TimeZoneInfo.Local);
        Write(projects.ResolveWorkspacePath(project), "cost-log.jsonl",
            JsonSerializer.Serialize(new CostLogEntry(utcInstant, "agent", null, "model", 1, 1, 0, 0, 3m, 1, 0,
                ProjectSlug: project.Slug, RunId: "local-day")));

        await service.RefreshAsync();

        var included = await service.GetReportAsync(new(localDay, localDay));
        var excluded = await service.GetReportAsync(new(localDay.AddDays(-1), localDay.AddDays(-1)));

        Assert.Equal(3m, included.TotalUsd);
        Assert.Equal(0m, excluded.TotalUsd);
    }

    [Fact]
    public async Task LoadsDurableSnapshotWithoutRescanningAndStartsSafelyWithoutOne()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var tickets = new TicketService(projects, new MemberService(projects));
        var empty = new CostReportService(projects, new PipelineService(projects), tickets);
        var day = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal(0m, (await empty.GetReportAsync(new(day, day))).TotalUsd);

        var project = await projects.CreateProjectAsync("Persistent costs");
        Write(projects.ResolveWorkspacePath(project), "cost-log.jsonl",
            Entry(day, 7m, project.Slug, pipeline: 1, run: "persisted"));
        await empty.RefreshAsync();
        File.Delete(Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "channel", "cost-log.jsonl"));

        var restarted = new CostReportService(projects, new PipelineService(projects), tickets);
        Assert.Equal(7m, (await restarted.GetReportAsync(new(day, day))).TotalUsd);
    }

    private static string Entry(DateOnly day, decimal cost, string slug, int? pipeline, bool estimated = false, string run = "", int? ticketId = null)
        => JsonSerializer.Serialize(new CostLogEntry(day.ToDateTime(new TimeOnly(12, 0)), "agent", ticketId, "model", 1, 1, 0, 0, cost, 1, 0, estimated, slug, pipeline, run));

    private static void Write(string workspace, string file, params string[] lines)
    {
        var directory = Path.Combine(workspace, ".agents", "channel");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, file), lines);
    }
}
