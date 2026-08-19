using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Services;

public sealed record CostReportFilter(DateOnly From, DateOnly To, IReadOnlySet<string>? ProjectSlugs = null, string? PipelineKey = null, string? Model = null);
public sealed record CostProjectOption(string Slug, string Name);
public sealed record CostPipelineOption(string Key, string Name);
public sealed record CostModelOption(string Key, string Name);
public sealed record CostBucket(
    DateOnly Day,
    string ProjectSlug,
    string ProjectName,
    decimal UsdCost,
    bool Estimated,
    decimal MeasuredUsd = 0,
    decimal EstimatedUsd = 0,
    long RtkSavedTokens = 0,
    decimal? RtkEstimatedUsd = null);
public sealed record CostProjectTotal(
    string ProjectSlug,
    string ProjectName,
    decimal UsdCost,
    bool Estimated,
    long RtkSavedTokens = 0,
    long RtkInputTokens = 0,
    int RtkCommands = 0,
    decimal? RtkEstimatedUsd = null);
public sealed record CostReport(decimal TotalUsd, bool Estimated, IReadOnlyList<CostBucket> Daily, IReadOnlyList<CostProjectTotal> Projects);
public sealed record CostReportOptions(IReadOnlyList<CostProjectOption> Projects, IReadOnlyList<CostPipelineOption> Pipelines, IReadOnlyList<CostModelOption> Models);

internal sealed record CostSnapshotRow(DateOnly Day, string ProjectSlug, string ProjectName, string? PipelineKey, string Model, long InputTokens, decimal UsdCost, bool Estimated);
internal sealed record RtkSavingsSnapshotRow(DateOnly Day, string ProjectSlug, string ProjectName, long InputTokens, long SavedTokens, int Commands);
internal sealed record CostReportSnapshot(
    int Version,
    DateTime GeneratedAt,
    CostReportOptions Options,
    IReadOnlyList<CostSnapshotRow> Rows,
    IReadOnlyList<RtkSavingsSnapshotRow> RtkSavings);

/// <summary>
/// Serves cost reports from a compact, durable snapshot. Historical JSONL files are only
/// scanned by <see cref="RefreshAsync"/>, never while a page is being rendered.
/// </summary>
public sealed class CostReportService
{
    private const int SnapshotVersion = 4;
    private const string UnknownPipelineSuffix = "unknown";
    private static readonly CostReportOptions EmptyOptions = new([], [], []);
    private static readonly CostReportSnapshot EmptySnapshot = new(SnapshotVersion, DateTime.MinValue, EmptyOptions, [], []);
    private readonly ProjectService _projects;
    private readonly PipelineService _pipelines;
    private readonly TicketService _tickets;
    private readonly IRtkSavingsReader? _rtkSavings;
    private readonly string _snapshotPath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, CostReport> _reportCache = new(StringComparer.Ordinal);
    private CostReportSnapshot _snapshot;

    public CostReportService(ProjectService projects, PipelineService pipelines, TicketService tickets)
        : this(projects, pipelines, tickets, null) { }

    public CostReportService(
        ProjectService projects,
        PipelineService pipelines,
        TicketService tickets,
        IRtkSavingsReader? rtkSavings)
    {
        _projects = projects;
        _pipelines = pipelines;
        _tickets = tickets;
        _rtkSavings = rtkSavings;
        _snapshotPath = Path.Combine(projects.DataDir, "cost-report-snapshot.json");
        _snapshot = LoadPersistedSnapshot() ?? EmptySnapshot;
    }

    public Task<CostReportOptions> GetOptionsAsync()
        => Task.FromResult(Volatile.Read(ref _snapshot).Options);

    public Task<CostReport> GetReportAsync(CostReportFilter filter)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var cacheKey = $"{snapshot.GeneratedAt.Ticks}|{BuildReportCacheKey(filter)}";
        return Task.FromResult(_reportCache.GetOrAdd(cacheKey, _ => BuildReport(snapshot, filter)));
    }

    public void RequestRefresh() => _refreshRequests.Writer.TryWrite(true);

    internal async ValueTask WaitForRefreshRequestAsync(CancellationToken cancellationToken)
    {
        await _refreshRequests.Reader.ReadAsync(cancellationToken);
        while (_refreshRequests.Reader.TryRead(out _)) { }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await BuildSnapshotAsync(cancellationToken);
            await PersistSnapshotAsync(snapshot, cancellationToken);
            Volatile.Write(ref _snapshot, snapshot);
            _reportCache.Clear();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private CostReportSnapshot? LoadPersistedSnapshot()
    {
        if (!File.Exists(_snapshotPath)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<CostReportSnapshot>(File.ReadAllText(_snapshotPath));
            return snapshot is { Version: SnapshotVersion } ? snapshot : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<CostReportSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var allProjects = await _projects.ListProjectsAsync();
        var rows = new List<CostSnapshotRow>();
        var rtkRows = new List<RtkSavingsSnapshotRow>();
        var pipelineOptions = new List<CostPipelineOption>();
        var seenRuns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in allProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var pipeline in await _pipelines.ListAsync(project.Slug))
                    pipelineOptions.Add(new($"{project.Slug}:{pipeline.Id}", $"{project.Name} / {pipeline.Name}"));

                var workspace = _projects.ResolveWorkspacePath(project);
                if (project.RtkEnabled && _rtkSavings is not null)
                {
                    foreach (var saving in await _rtkSavings.ReadDailyAsync(workspace, cancellationToken))
                        rtkRows.Add(new(saving.Day, project.Slug, project.Name, saving.InputTokens, saving.SavedTokens, saving.Commands));
                }

                var directory = Path.Combine(workspace, ".agents", "channel");
                if (!Directory.Exists(directory))
                {
                    await Task.Yield();
                    continue;
                }

                var entries = new List<CostLogEntry>();
                foreach (var file in Directory.EnumerateFiles(directory, "cost-log*.jsonl").OrderBy(x => x, StringComparer.Ordinal))
                foreach (var line in File.ReadLines(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CostLogEntry? entry;
                    try { entry = JsonSerializer.Deserialize<CostLogEntry>(line); } catch { continue; }
                    if (entry is null || (entry.RunId is not null && !seenRuns.Add(entry.RunId))) continue;
                    entries.Add(entry);
                }

                var legacyTicketIds = entries
                    .Where(entry => entry.PipelineId is null && entry.TicketId is not null)
                    .Select(entry => entry.TicketId!.Value)
                    .Distinct()
                    .ToList();
                var ticketPipelines = await _tickets.GetPipelineIdsAsync(project.Slug, legacyTicketIds);
                var hasUnknownPipeline = false;

                foreach (var entry in entries)
                {
                    var pipelineId = entry.PipelineId;
                    var pipelineUnknown = false;
                    if (pipelineId is null && entry.TicketId is int ticketId)
                        pipelineUnknown = !ticketPipelines.TryGetValue(ticketId, out pipelineId);

                    hasUnknownPipeline |= pipelineUnknown;
                    var pipelineKey = pipelineUnknown
                        ? $"{project.Slug}:{UnknownPipelineSuffix}"
                        : pipelineId is int resolvedPipelineId ? $"{project.Slug}:{resolvedPipelineId}" : null;
                    rows.Add(new(
                        DateOnly.FromDateTime(entry.At.ToLocalTime()),
                        entry.ProjectSlug ?? project.Slug,
                        project.Name,
                        pipelineKey,
                        string.IsNullOrWhiteSpace(entry.Model) ? "default" : entry.Model,
                        entry.InputTokens,
                        entry.UsdCost,
                        entry.CostEstimated));
                }

                if (hasUnknownPipeline)
                    pipelineOptions.Add(new($"{project.Slug}:{UnknownPipelineSuffix}", project.Name));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A removed or temporarily unavailable project must not break the snapshot.
            }

            // Keep the scan cooperative and explicitly sequential across projects.
            await Task.Yield();
        }

        // Estimated stays in the group key so daily reports can split measured from estimated cost.
        var compactRows = rows
            .GroupBy(row => new { row.Day, row.ProjectSlug, row.ProjectName, row.PipelineKey, row.Model, row.Estimated })
            .Select(group => new CostSnapshotRow(
                group.Key.Day,
                group.Key.ProjectSlug,
                group.Key.ProjectName,
                group.Key.PipelineKey,
                group.Key.Model,
                group.Sum(row => row.InputTokens),
                group.Sum(row => row.UsdCost),
                group.Key.Estimated))
            .OrderBy(row => row.Day)
            .ThenBy(row => row.ProjectName)
            .ToList();

        var options = new CostReportOptions(
            allProjects.Select(project => new CostProjectOption(project.Slug, project.Name)).ToList(),
            pipelineOptions.OrderBy(option => option.Name).ToList(),
            compactRows.Select(row => row.Model)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(model => new CostModelOption(model, model))
                .ToList());
        var compactRtkRows = rtkRows
            .GroupBy(row => new { row.Day, row.ProjectSlug, row.ProjectName })
            .Select(group => new RtkSavingsSnapshotRow(
                group.Key.Day,
                group.Key.ProjectSlug,
                group.Key.ProjectName,
                group.Sum(row => row.InputTokens),
                group.Sum(row => row.SavedTokens),
                group.Sum(row => row.Commands)))
            .OrderBy(row => row.Day)
            .ThenBy(row => row.ProjectName)
            .ToList();
        return new(SnapshotVersion, DateTime.UtcNow, options, compactRows, compactRtkRows);
    }

    private async Task PersistSnapshotAsync(CostReportSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{_snapshotPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken);
            File.Move(temporaryPath, _snapshotPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static CostReport BuildReport(CostReportSnapshot snapshot, CostReportFilter filter)
    {
        if (filter.To < filter.From) return new(0, false, [], []);
        var rows = snapshot.Rows.Where(row =>
            row.Day >= filter.From && row.Day <= filter.To
            && (filter.ProjectSlugs is not { Count: > 0 } || filter.ProjectSlugs.Contains(row.ProjectSlug))
            && (filter.PipelineKey is null || filter.PipelineKey == row.PipelineKey)
            && (filter.Model is null || string.Equals(filter.Model, row.Model, StringComparison.OrdinalIgnoreCase))).ToList();
        var daily = rows.GroupBy(row => new { row.Day, row.ProjectSlug, row.ProjectName })
            .Select(group => new CostBucket(group.Key.Day, group.Key.ProjectSlug, group.Key.ProjectName,
                group.Sum(row => row.UsdCost), group.Any(row => row.Estimated),
                group.Where(row => !row.Estimated).Sum(row => row.UsdCost),
                group.Where(row => row.Estimated).Sum(row => row.UsdCost)))
            .ToList();
        var totalsByProject = rows.GroupBy(row => new { row.ProjectSlug, row.ProjectName })
            .Select(group => new CostProjectTotal(group.Key.ProjectSlug, group.Key.ProjectName,
                group.Sum(row => row.UsdCost), group.Any(row => row.Estimated)))
            .ToDictionary(total => total.ProjectSlug, StringComparer.Ordinal);

        // RTK does not associate a gain entry with a KittyClaw pipeline or model. Hiding
        // savings under those filters prevents a project-wide estimate from looking scoped.
        if (filter.PipelineKey is null && filter.Model is null)
        {
            var savings = snapshot.RtkSavings.Where(row =>
                row.Day >= filter.From && row.Day <= filter.To
                && (filter.ProjectSlugs is not { Count: > 0 } || filter.ProjectSlugs.Contains(row.ProjectSlug)));
            foreach (var group in savings.GroupBy(row => new { row.ProjectSlug, row.ProjectName }))
            {
                totalsByProject.TryGetValue(group.Key.ProjectSlug, out var current);
                var savedTokens = group.Sum(row => row.SavedTokens);
                var inputTokens = group.Sum(row => row.InputTokens);
                // One period-weighted rate per project keeps the daily segments summing to the card total.
                var ratePerToken = EstimateRtkInputRatePerToken(rows, group.Key.ProjectSlug);
                totalsByProject[group.Key.ProjectSlug] = new CostProjectTotal(
                    group.Key.ProjectSlug,
                    group.Key.ProjectName,
                    current?.UsdCost ?? 0,
                    current?.Estimated ?? false,
                    savedTokens,
                    inputTokens,
                    group.Sum(row => row.Commands),
                    ratePerToken is decimal totalRate ? savedTokens * totalRate : null);

                foreach (var saving in group)
                {
                    var dayUsd = ratePerToken is decimal rate ? saving.SavedTokens * rate : (decimal?)null;
                    var index = daily.FindIndex(bucket => bucket.Day == saving.Day && bucket.ProjectSlug == saving.ProjectSlug);
                    if (index >= 0)
                        daily[index] = daily[index] with { RtkSavedTokens = saving.SavedTokens, RtkEstimatedUsd = dayUsd };
                    else
                        daily.Add(new CostBucket(saving.Day, saving.ProjectSlug, saving.ProjectName, 0, false, 0, 0,
                            saving.SavedTokens, dayUsd));
                }
            }
        }

        daily = daily
            .OrderBy(bucket => bucket.Day)
            .ThenBy(bucket => bucket.ProjectName)
            .ToList();
        var totals = totalsByProject.Values
            .OrderByDescending(total => total.UsdCost)
            .ThenByDescending(total => total.RtkSavedTokens)
            .ToList();
        return new(rows.Sum(row => row.UsdCost), rows.Any(row => row.Estimated), daily, totals);
    }

    private static decimal? EstimateRtkInputRatePerToken(IReadOnlyList<CostSnapshotRow> rows, string projectSlug)
    {
        decimal weightedRates = 0;
        long pricedInputTokens = 0;
        foreach (var row in rows.Where(row => row.ProjectSlug == projectSlug && row.InputTokens > 0))
        {
            if (!ModelCostEstimator.TryEstimate(row.Model, 1_000_000, 0, 0, 0, out var inputRate)) continue;
            weightedRates += inputRate * row.InputTokens;
            pricedInputTokens += row.InputTokens;
        }

        return pricedInputTokens == 0
            ? null
            : weightedRates / pricedInputTokens / 1_000_000m;
    }

    private static string BuildReportCacheKey(CostReportFilter filter)
    {
        var projectsKey = filter.ProjectSlugs is null
            ? string.Empty
            : string.Join('\n', filter.ProjectSlugs.Order(StringComparer.Ordinal));
        return $"{filter.From:O}|{filter.To:O}|{filter.PipelineKey}|{filter.Model}|{projectsKey}";
    }
}
