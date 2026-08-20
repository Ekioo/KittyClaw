using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Reconciles persisted integration jobs. A dirty target checkout never stops isolated runs:
/// the job remains visible and is retried after the external changes have been resolved.
/// </summary>
public sealed class WorktreeMergeQueueProcessor(
    ProjectService projects,
    WorktreeMergeQueueService queue,
    ILogger<WorktreeMergeQueueProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var project in await projects.ListProjectsAsync())
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (!project.WorktreesEnabled) continue;
                try
                {
                    await queue.RecoverTerminalWorktreesAsync(project.Slug, stoppingToken);
                    await queue.ProcessNextAsync(project.Slug, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Unable to reconcile durable integrations for {ProjectSlug}", project.Slug);
                }
            }

            try { await Task.Delay(PollDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
