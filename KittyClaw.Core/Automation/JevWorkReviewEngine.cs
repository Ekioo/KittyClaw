using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

public sealed class JevWorkReviewEngine(ProjectService projects, ColumnProcessorService processors,
    TicketService tickets, JevWorkReviewService reviews, StartupWorkGate startup,
    ILogger<JevWorkReviewEngine> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startup.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var project in (await projects.ListProjectsAsync()).Where(p => !p.IsPaused))
                foreach (var processor in (await processors.ListAsync(project.Slug)).Where(p => ColumnWorkFlow.Resolve(ColumnProcessingState.From(p)) is ColumnWorkState.AwaitingOwnerReview or ColumnWorkState.AwaitingProfileApproval))
                foreach (var summary in (await tickets.ListTicketsAsync(project.Slug)).Where(t => t.ColumnId == processor.ColumnId))
                {
                    var ticket = await tickets.GetTicketAsync(project.Slug, summary.Id);
                    if (ticket is not null) await reviews.ObserveAsync(project.Slug, processor, ticket, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("JEV work review sweep failed; retrying on the next sweep."); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
