using KittyClaw.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

public sealed class JevObservationEngine(ProjectService projects, ColumnProcessorService processors,
    TicketService tickets, JevQualificationService qualification,
    ILogger<JevObservationEngine> logger, StartupWorkGate startupGate) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startupGate.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var project in (await projects.ListProjectsAsync()).Where(p => !p.IsPaused))
                {
                    foreach (var processor in (await processors.ListAsync(project.Slug)).Where(p => p.Enabled && p.JevObservationOnly))
                    {
                        await using var db = projects.GetProjectDb(project.Slug);
                        var destinations = await db.BoardColumns.AsNoTracking().ToListAsync(stoppingToken);
                        foreach (var summary in (await tickets.ListTicketsAsync(project.Slug)).Where(t => t.ColumnId == processor.ColumnId))
                        {
                            var ticket = await tickets.GetTicketAsync(project.Slug, summary.Id);
                            if (ticket?.ColumnId != processor.ColumnId) continue;
                            await qualification.ObserveAsync(project.Slug, processor, ticket, destinations, stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("JEV observation sweep failed; retrying on the next sweep."); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
