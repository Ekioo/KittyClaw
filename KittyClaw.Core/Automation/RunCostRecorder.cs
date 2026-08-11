using KittyClaw.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Turns completed runs into durable cost records. Subscribes to run completion and, for every
/// run that reported token usage, appends a <see cref="CostLogEntry"/> to the workspace cost-log
/// (the source the daily-budget gate reads) and accumulates token/USD totals onto the ticket
/// (the per-ticket figure shown on the board, which must survive the 24h run-log purge).
/// </summary>
public sealed class RunCostRecorder : IHostedService
{
    private readonly AgentRunRegistry _runs;
    private readonly CostTracker _cost;
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly ILogger<RunCostRecorder> _logger;

    public RunCostRecorder(AgentRunRegistry runs, CostTracker cost, ProjectService projects,
        TicketService tickets, ILogger<RunCostRecorder> logger)
    {
        _runs = runs;
        _cost = cost;
        _projects = projects;
        _tickets = tickets;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runs.OnRunEnded += OnRunEnded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runs.OnRunEnded -= OnRunEnded;
        return Task.CompletedTask;
    }

    // OnRunEnded is raised synchronously from AgentRunRegistry.Complete — hand off to a task so
    // the file/DB writes never block the runner's completion path.
    private void OnRunEnded(AgentRun run)
    {
        if (!run.HasUsage) return;
        _ = Task.Run(() => RecordAsync(run));
    }

    internal async Task RecordAsync(AgentRun run)
    {
        try
        {
            var endedAt = run.EndedAt ?? DateTime.UtcNow;
            var project = await _projects.GetProjectAsync(run.ProjectSlug);
            if (project is not null)
            {
                var workspace = _projects.ResolveWorkspacePath(project);
                int? pipelineId = null;
                if (run.TicketId is int snapshotTicketId)
                    pipelineId = (await _tickets.GetTicketAsync(run.ProjectSlug, snapshotTicketId))?.PipelineId;
                _cost.LogRun(workspace, new CostLogEntry(
                    At: endedAt,
                    Agent: run.AgentName,
                    TicketId: run.TicketId,
                    Model: run.Model ?? "default",
                    InputTokens: run.InputTokens,
                    OutputTokens: run.OutputTokens,
                    CacheReadTokens: run.CacheReadTokens,
                    CacheWriteTokens: run.CacheWriteTokens,
                    UsdCost: run.TotalCostUsd ?? 0m,
                    DurationSeconds: (endedAt - run.StartedAt).TotalSeconds,
                    ExitCode: run.ExitCode ?? -1,
                    CostEstimated: run.CostIsEstimated,
                    ProjectSlug: run.ProjectSlug,
                    PipelineId: pipelineId,
                    RunId: run.RunId));
            }

            if (run.TicketId is int ticketId)
                await _tickets.AddAgentUsageAsync(run.ProjectSlug, ticketId,
                    run.TotalTokens, (double)(run.TotalCostUsd ?? 0m), run.CostIsEstimated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record cost for run {RunId} ({Agent})", run.RunId, run.AgentName);
        }
    }
}
