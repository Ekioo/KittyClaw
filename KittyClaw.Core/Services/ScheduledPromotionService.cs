using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>
/// Background service (feature #99) that promotes "Scheduled" tickets to their target column
/// once their <c>FireAt</c> instant is reached. This lets calendar-dated work (future posts,
/// dated gates) live in a dedicated column instead of polluting "Blocked".
/// </summary>
public sealed class ScheduledPromotionService : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly ILogger<ScheduledPromotionService> _logger;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    public ScheduledPromotionService(
        ProjectService projects,
        TicketService tickets,
        ILogger<ScheduledPromotionService> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledPromotionService started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PromoteDueAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "ScheduledPromotionService tick failed"); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Promotes every due Scheduled ticket across all non-paused projects. Returns the number of
    /// tickets promoted. Exposed for deterministic testing (pass a fixed <paramref name="now"/>).
    /// </summary>
    internal async Task<int> PromoteDueAsync(DateTime now, CancellationToken ct = default)
    {
        var promoted = 0;
        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) break;
            if (project.IsPaused) continue;

            var dueIds = await _tickets.ListDueScheduledTicketIdsAsync(project.Slug, now);
            foreach (var id in dueIds)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var ticket = await _tickets.PromoteScheduledAsync(project.Slug, id);
                    if (ticket is not null)
                    {
                        promoted++;
                        _logger.LogInformation(
                            "Promoted scheduled ticket {Project}#{Id} → {Status}",
                            project.Slug, id, ticket.Status);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to promote scheduled ticket {Project}#{Id}", project.Slug, id);
                }
            }
        }
        return promoted;
    }
}
