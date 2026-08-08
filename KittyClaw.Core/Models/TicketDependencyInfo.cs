namespace KittyClaw.Core.Models;

/// <summary>
/// Summary of one end of a dependency edge, returned inside <see cref="Ticket.BlockedBy"/>
/// and <see cref="Ticket.Blocks"/>.
/// </summary>
public record TicketDependencyInfo(int DependencyId, int TicketId, string Title, string Status);
