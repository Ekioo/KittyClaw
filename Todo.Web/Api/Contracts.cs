using Todo.Core.Models;

namespace Todo.Web.Api;

public record CreateProjectRequest(string Name);
public record CreateTicketRequest(string Title, string Description = "", string CreatedBy = "owner", string Status = "Backlog", List<int>? LabelIds = null, TicketPriority Priority = TicketPriority.NiceToHave, string? AssignedTo = null);
public record UpdateTicketRequest(string? Title = null, string? Description = null, string Author = "owner", TicketPriority? Priority = null, string? AssignedTo = null);
public record MoveTicketRequest(string Status);
public record AddCommentRequest(string Content, string Author = "owner");
public record UpdateCommentRequest(string Content, string Author = "owner");
public record CreateLabelRequest(string Name, string Color = "#6366f1");
public record SetTicketLabelsRequest(List<int> LabelIds);
public record ReorderTicketRequest(string Status, int Index);
public record CreateColumnRequest(string Name, string Color = "#5a6a80");
