using KittyClaw.Core.Models;

namespace KittyClaw.Web.Api;

public record CreateProjectRequest(string Name);
public record CreateTicketRequest(string Title, string CreatedBy, string Status, string Description = "", List<int>? LabelIds = null, TicketPriority Priority = TicketPriority.NiceToHave, string? AssignedTo = null, int? ParentId = null);
public record UpdateTicketRequest(string Author, string? Title = null, string? Description = null, TicketPriority? Priority = null, string? AssignedTo = null, List<int>? LabelIds = null);
public record MoveTicketRequest(string Status, string Author);
public record AddCommentRequest(string Content, string Author);
public record UpdateCommentRequest(string Content, string Author);
public record CreateLabelRequest(string Name, string Color = "#6366f1");
public record UpdateLabelRequest(string? Name = null, string? Color = null);
public record SetTicketLabelsRequest(List<int> LabelIds);
public record ReorderTicketRequest(string Status, int Index);
public record CreateColumnRequest(string Name, string Color = "#5a6a80");
public record UpdateColumnRequest(string? Name = null, string? Color = null);
public record ReorderColumnRequest(int ColumnId, int Index);
public record CreateMemberRequest(string Name);
public record UpdateMemberRequest(string? Name = null);
public record SetParentRequest(int ParentId);
public record UpdateProjectRequest(string? WorkspacePath = null, string? FallbackModel = null, bool UpdateFallbackModel = false);
public record SteerRunRequest(string Text);
public record BrowseFolderRequest(string? InitialPath = null);
public record ChatImageDto(string DataUrl, string Mime, string Name, long SizeBytes);
public record ChatStartRequest(string Message, string Target = "owner-chat", bool ForceNew = false, int? TicketId = null, IReadOnlyList<ChatImageDto>? Images = null);
public record ChatTargetDto(string Slug, string Name, string Kind);
public record ChatTargetsResponse(string? LastTarget, List<ChatTargetDto> Targets);
public record ChatMessageDto(string Role, string Text, string? ToolName, string? Detail, string CreatedAt);
public record AddTileRequest(string TileSlug); // required -- folder name (slug) for the new tile
public record MoveTileRequest(int X, int Y); // required — pixel coords snapped to 20px grid
public record ResizeTileRequest(int Width, int Height); // required — pixels snapped to 20px grid
