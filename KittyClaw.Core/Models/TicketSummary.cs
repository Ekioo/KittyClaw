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
    public DateTime? DueDate { get; init; }
}

public record SubTicketInfo(int Id, string Title, string Status, string? AssignedTo);
