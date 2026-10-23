using KittyClaw.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

public sealed class JevObservationEngine(ProjectService projects, ColumnProcessorService processors,
    TicketService tickets, ColumnExecutionService executions, JevQualificationService qualification,
    ILogger<JevObservationEngine> logger, StartupWorkGate startupGate, JevProposalDecisionService decisions) : BackgroundService
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
                    foreach (var processor in (await processors.ListAsync(project.Slug)).Where(p => p.Enabled && (p.JevObservationOnly || p.JevPostRunObservation)))
                    {
                        await using var db = projects.GetProjectDb(project.Slug);
                        var destinations = await db.BoardColumns.AsNoTracking().ToListAsync(stoppingToken);
                        if (processor.JevPostRunObservation && !processor.JevObservationOnly)
                        {
                            // Initialize the existing execution schema, then scan only executions ending after opt-in.
                            await executions.ListAsync(project.Slug);
                            var completed = await db.ColumnExecutions.AsNoTracking()
                                .Where(e => e.ProcessorId == processor.Id && e.EndedAt >= processor.JevPostRunObservationSince &&
                                    e.RunId != null && (e.Status == KittyClaw.Core.Models.ColumnExecutionStatus.Completed || e.Status == KittyClaw.Core.Models.ColumnExecutionStatus.Failed || e.Status == KittyClaw.Core.Models.ColumnExecutionStatus.Cancelled))
                                .OrderBy(e => e.EndedAt).ToListAsync(stoppingToken);
                            foreach (var execution in completed)
                            {
                                var ticket = await tickets.GetTicketAsync(project.Slug, execution.TicketId);
                                if (ticket is not null)
                                {
                                    await qualification.ObserveAsync(project.Slug, processor, ticket, destinations, stoppingToken, execution);
                                    // Post-run snapshots are retained for the comment timeline.
                                    // Live routing is decided before the execution transition.
                                }
                            }
                        }
                        if (!processor.JevObservationOnly) continue;
                        foreach (var summary in (await tickets.ListTicketsAsync(project.Slug)).Where(t => t.ColumnId == processor.ColumnId))
                        {
                            await executions.ListAsync(project.Slug);
                            if (await db.ColumnExecutions.AnyAsync(e => e.TicketId == summary.Id && e.JevClassicFallback &&
                                (e.Status == KittyClaw.Core.Models.ColumnExecutionStatus.Running || e.Status == KittyClaw.Core.Models.ColumnExecutionStatus.Retrying || e.Status == KittyClaw.Core.Models.ColumnExecutionStatus.WaitingForChildren), stoppingToken)) continue;
                            var ticket = await tickets.GetTicketAsync(project.Slug, summary.Id);
                            if (ticket?.ColumnId != processor.ColumnId) continue;
                            var previous = await qualification.GetAsync(project.Slug, ticket.Id, stoppingToken);
                            var result = previous?.TicketUpdatedAt == ticket.UpdatedAt && previous.SourceColumnId == processor.ColumnId ? previous.RoutingResult : null;
                            await qualification.ObserveAsync(project.Slug, processor, ticket, destinations, stoppingToken, routingResult: result);
                            var proposal = await qualification.GetAsync(project.Slug, ticket.Id, stoppingToken);
                            if (processor.UsesAutomaticJevRouting && proposal is not null)
                                await decisions.TryApplyAutomaticallyAsync(project.Slug, ticket.Id, JevDecisionStore.ProposalId(proposal));
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
