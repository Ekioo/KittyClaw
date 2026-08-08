namespace KittyClaw.Core.Models;

/// <summary>
/// A directed "blocked-by" edge: <see cref="BlockedTicketId"/> cannot be dispatched
/// until <see cref="BlocksTicketId"/> reaches a success column.
/// </summary>
public class TicketDependency
{
    public int Id { get; set; }
    /// <summary>The ticket that is blocked.</summary>
    public int BlockedTicketId { get; set; }
    /// <summary>The ticket that must complete first (the blocker).</summary>
    public int BlocksTicketId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
