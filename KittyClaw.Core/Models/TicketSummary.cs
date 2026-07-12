namespace KittyClaw.Core.Models;

public record TicketSummary(
    int Id,
    string Title,
    string Description,
    string Status,
    TicketPriority Priority,
    int SortOrder,
    string? AssignedTo,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<Label> Labels,
    int CommentCount,
    DateTime? LastActivityAt,
    int? ParentId,
    List<SubTicketInfo> SubTickets)
{
    /// <summary>Auto-promotion instant for Scheduled tickets (UTC), null otherwise.</summary>
    public DateTime? FireAt { get; init; }

    /// <summary>Column a Scheduled ticket promotes to when it fires.</summary>
    public string? ScheduleTarget { get; init; }

    /// <summary>Cumulative tokens consumed by agent runs on this ticket (all token classes).</summary>
    public long AgentTokens { get; init; }

    /// <summary>Cumulative USD cost of agent runs on this ticket, as priced by the claude CLI.</summary>
    public double AgentCostUsd { get; init; }
}

public record SubTicketInfo(int Id, string Title, string Status, string? AssignedTo);
