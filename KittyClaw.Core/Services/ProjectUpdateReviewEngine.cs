using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

public sealed class ProjectUpdateReviewEngine(
    ProjectService projects, ProjectUpdateReviewService reviews, StartupWorkGate startup,
    ILogger<ProjectUpdateReviewEngine> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startup.WaitAsync(stoppingToken);
        var notes = ProjectUpdateReviewService.ReadBundledNotes();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            do
            {
                try
                {
                    foreach (var project in await projects.ListProjectsAsync())
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(project.WorkspacePath)) continue;
                        try { await reviews.CheckAsync(project.Slug, notes, stoppingToken); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { logger.LogWarning(ex, "Could not queue update review for {Project}", project.Slug); }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Project update review scan failed"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
