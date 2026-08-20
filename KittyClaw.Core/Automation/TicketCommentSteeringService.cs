using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Forwards new owner comments to every active agent run working on the same ticket.
/// Agent and automation comments are deliberately ignored to prevent feedback loops.
/// </summary>
public sealed class TicketCommentSteeringService : IHostedService
{
    private readonly Services.TicketService _tickets;
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<TicketCommentSteeringService> _logger;

    public TicketCommentSteeringService(
        Services.TicketService tickets,
        AgentRunRegistry runs,
        ILogger<TicketCommentSteeringService> logger)
    {
        _tickets = tickets;
        _runs = runs;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tickets.TicketCommentAdded += OnTicketCommentAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tickets.TicketCommentAdded -= OnTicketCommentAdded;
        return Task.CompletedTask;
    }

    private void OnTicketCommentAdded(
        string projectSlug,
        int ticketId,
        int commentId,
        string author,
        string content)
    {
        if (!string.Equals(author, "owner", StringComparison.OrdinalIgnoreCase))
            return;

        var message = $"New owner comment on ticket #{ticketId} (comment #{commentId}):\n{content}";
        foreach (var run in _runs.ActiveForTicket(projectSlug, ticketId))
        {
            if (!run.SteeringQueue.Writer.TryWrite(message))
            {
                _logger.LogWarning(
                    "Could not steer owner comment #{CommentId} to active run {RunId} for ticket #{TicketId}",
                    commentId,
                    run.RunId,
                    ticketId);
            }
        }
    }
}
