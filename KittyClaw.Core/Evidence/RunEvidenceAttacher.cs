using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Evidence;

/// <summary>
/// Captures and attaches evidence to ticket and run records when an agent run ends.
/// Subscribes to <see cref="AgentRunRegistry.OnRunEnded"/> and, for every ticket-scoped run,
/// calls <see cref="RunEvidenceCapture.CaptureAsync"/> then
/// <see cref="EvidenceStore.MergeAndSave"/> so the evidence bundle is durable
/// before the run log is eligible for purge.
/// </summary>
public sealed class RunEvidenceAttacher : IHostedService
{
    private readonly AgentRunRegistry _runs;
    private readonly EvidenceStore _store;
    private readonly ProjectService _projects;
    private readonly ILogger<RunEvidenceAttacher> _logger;

    public RunEvidenceAttacher(AgentRunRegistry runs, EvidenceStore store,
        ProjectService projects, ILogger<RunEvidenceAttacher> logger)
    {
        _runs = runs;
        _store = store;
        _projects = projects;
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

    // OnRunEnded fires synchronously from AgentRunRegistry.Complete; hand off to a background
    // task so file writes never block the runner's completion path.
    private void OnRunEnded(AgentRun run)
    {
        if (run.TicketId is null) return;
        _ = Task.Run(() => AttachAsync(run));
    }

    internal async Task AttachAsync(AgentRun run)
    {
        try
        {
            var project = await _projects.GetProjectAsync(run.ProjectSlug);
            if (project is null)
            {
                _logger.LogWarning("RunEvidenceAttacher: project {Slug} not found for run {RunId}",
                    run.ProjectSlug, run.RunId);
                return;
            }

            var workspacePath = _projects.ResolveWorkspacePath(project);
            var routing = ModelRouting.Resolve(run.Model, project.LocalModelBaseUrl);
            var providerName = RunEvidenceCapture.ProviderName(routing.Provider);

            var evidence = await RunEvidenceCapture.CaptureAsync(run, workspacePath, providerName);
            _store.MergeAndSave(evidence);

            _logger.LogInformation(
                "RunEvidenceAttacher: attached evidence for run {RunId} ticket #{TicketId} status={Status}",
                run.RunId, run.TicketId, evidence.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunEvidenceAttacher: failed to attach evidence for run {RunId}", run.RunId);
        }
    }
}
