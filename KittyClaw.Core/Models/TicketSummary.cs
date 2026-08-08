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

    /// <summary>Cumulative USD cost of agent runs on this ticket.</summary>
    public double AgentCostUsd { get; init; }

    public bool AgentCostEstimated { get; init; }

    public int PipelineId { get; init; }
    public int? ColumnId { get; init; }
    public bool BlocksParent { get; init; } = true;
    /// <summary>Count of blockers whose status is not "Done" (unresolved).</summary>
    public int UnresolvedBlockerCount { get; init; }
}

public record SubTicketInfo(int Id, string Title, string Status, string? AssignedTo)
{
    public int PipelineId { get; init; }
    public int? ColumnId { get; init; }
    public bool BlocksParent { get; init; } = true;
    public ColumnRole ColumnRole { get; init; } = ColumnRole.Normal;
}
