namespace KittyClaw.Core.Models;

/// <summary>
/// Summary of one end of a dependency edge, returned inside <see cref="Ticket.BlockedBy"/>
/// and <see cref="Ticket.Blocks"/>.
/// </summary>
public record TicketDependencyInfo(int DependencyId, int TicketId, string Title, string Status)
{
    /// <summary>
    /// Semantic role of the dependency's current column. Consumers must use this rather
    /// than a localized or project-specific status name to decide whether it is resolved.
    /// </summary>
    public ColumnRole ColumnRole { get; init; } = ColumnRole.Normal;
}
