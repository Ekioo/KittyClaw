using System.Text.Json;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Services;

public sealed record CostReportFilter(DateOnly From, DateOnly To, IReadOnlySet<string>? ProjectSlugs = null, string? PipelineKey = null);
public sealed record CostProjectOption(string Slug, string Name);
public sealed record CostPipelineOption(string Key, string Name);
public sealed record CostBucket(DateOnly Day, string ProjectSlug, string ProjectName, decimal UsdCost, bool Estimated);
public sealed record CostProjectTotal(string ProjectSlug, string ProjectName, decimal UsdCost, bool Estimated);
public sealed record CostReport(decimal TotalUsd, bool Estimated, IReadOnlyList<CostBucket> Daily, IReadOnlyList<CostProjectTotal> Projects);
public sealed record CostReportOptions(IReadOnlyList<CostProjectOption> Projects, IReadOnlyList<CostPipelineOption> Pipelines);

/// <summary>Reads current and rotated durable JSONL cost logs across registered projects.</summary>
public sealed class CostReportService(ProjectService projects, PipelineService pipelines, TicketService tickets)
{
    private const string UnknownPipelineSuffix = "unknown";

    public async Task<CostReportOptions> GetOptionsAsync()
    {
        var all = await projects.ListProjectsAsync();
        var pipelineOptions = new List<CostPipelineOption>();
        foreach (var project in all)
        {
            try
            {
                foreach (var pipeline in await pipelines.ListAsync(project.Slug))
                    pipelineOptions.Add(new($"{project.Slug}:{pipeline.Id}", $"{project.Name} / {pipeline.Name}"));
                if (await HasUnknownPipelineEntriesAsync(project))
                    pipelineOptions.Add(new($"{project.Slug}:{UnknownPipelineSuffix}", project.Name));
            }
            catch { /* A removed or temporarily unavailable project must not break the report. */ }
        }
        return new(all.Select(p => new CostProjectOption(p.Slug, p.Name)).ToList(),
            pipelineOptions.OrderBy(p => p.Name).ToList());
    }

    public async Task<CostReport> GetReportAsync(CostReportFilter filter)
    {
        if (filter.To < filter.From) return new(0, false, [], []);
        var rows = new List<(DateOnly Day, string Slug, string Name, decimal Cost, bool Estimated)>();
        var seenRuns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in await projects.ListProjectsAsync())
        {
            if (filter.ProjectSlugs is { Count: > 0 } && !filter.ProjectSlugs.Contains(project.Slug)) continue;
            try
            {
                var directory = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "channel");
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "cost-log*.jsonl").OrderBy(x => x, StringComparer.Ordinal))
                foreach (var line in File.ReadLines(file))
                {
                    CostLogEntry? entry;
                    try { entry = JsonSerializer.Deserialize<CostLogEntry>(line); } catch { continue; }
                    if (entry is null || (entry.RunId is not null && !seenRuns.Add(entry.RunId))) continue;
                    var day = DateOnly.FromDateTime(entry.At.ToLocalTime());
                    if (day < filter.From || day > filter.To) continue;
                    var pipelineId = entry.PipelineId;
                    var pipelineUnknown = false;
                    if (pipelineId is null && entry.TicketId is int ticketId)
                    {
                        var ticket = await tickets.GetTicketAsync(project.Slug, ticketId);
                        pipelineId = ticket?.PipelineId;
                        pipelineUnknown = ticket is null;
                    }
                    var pipelineKey = pipelineUnknown
                        ? $"{project.Slug}:{UnknownPipelineSuffix}"
                        : pipelineId is int resolvedPipelineId ? $"{project.Slug}:{resolvedPipelineId}" : null;
                    if (filter.PipelineKey is { } selected && selected != pipelineKey) continue;
                    rows.Add((day, entry.ProjectSlug ?? project.Slug, project.Name, entry.UsdCost, entry.CostEstimated));
                }
            }
            catch { /* Keep the remaining projects usable. */ }
        }
        var daily = rows.GroupBy(r => new { r.Day, r.Slug, r.Name })
            .Select(g => new CostBucket(g.Key.Day, g.Key.Slug, g.Key.Name, g.Sum(x => x.Cost), g.Any(x => x.Estimated)))
            .OrderBy(x => x.Day).ThenBy(x => x.ProjectName).ToList();
        var totals = rows.GroupBy(r => new { r.Slug, r.Name })
            .Select(g => new CostProjectTotal(g.Key.Slug, g.Key.Name, g.Sum(x => x.Cost), g.Any(x => x.Estimated)))
            .OrderByDescending(x => x.UsdCost).ToList();
        return new(rows.Sum(x => x.Cost), rows.Any(x => x.Estimated), daily, totals);
    }

    private async Task<bool> HasUnknownPipelineEntriesAsync(KittyClaw.Core.Models.Project project)
    {
        var directory = Path.Combine(projects.ResolveWorkspacePath(project), ".agents", "channel");
        if (!Directory.Exists(directory)) return false;
        foreach (var file in Directory.EnumerateFiles(directory, "cost-log*.jsonl"))
        foreach (var line in File.ReadLines(file))
        {
            CostLogEntry? entry;
            try { entry = JsonSerializer.Deserialize<CostLogEntry>(line); } catch { continue; }
            if (entry is { PipelineId: null, TicketId: int ticketId }
                && await tickets.GetTicketAsync(project.Slug, ticketId) is null)
                return true;
        }
        return false;
    }
}
