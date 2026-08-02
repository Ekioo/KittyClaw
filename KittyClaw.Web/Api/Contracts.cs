using KittyClaw.Core.Models;

namespace KittyClaw.Web.Api;

public record CreateProjectRequest(string Name);
public record CreatePipelineRequest(string Name);
public record UpdatePipelineRequest(string? Name = null);
public record CreateProjectSkillRequest(string Name, string Instructions = "", string? Description = null);
public record UpdateProjectSkillRequest(string? Name = null, string? Instructions = null, string? Description = null);
public record SaveColumnProcessorRequest(
    string Name,
    string Mission,
    string Prompt = "",
    string? Model = null,
    bool Enabled = true,
    int MaxTurns = 100,
    List<string>? AvailableSkills = null,
    List<string>? RecommendedSkills = null,
    List<string>? RequiredSkills = null,
    TicketSelectionOrder SelectionOrder = TicketSelectionOrder.Position,
    int MaxAttempts = 3,
    int RetryBackoffSeconds = 60,
    int? DefaultTargetColumnId = null,
    int? TechnicalFailureColumnId = null,
    List<ColumnRoute>? Routes = null);
public record CreateTicketRequest(
    string Title, string CreatedBy, string Status, string Description = "",
    List<int>? LabelIds = null, TicketPriority Priority = TicketPriority.NiceToHave,
    string? AssignedTo = null, int? ParentId = null, int? PipelineId = null,
    int? ColumnId = null, bool BlocksParent = true);
// The root PATCH applies every provided field — Status included — in ONE atomic write,
// through the same semantics as the dedicated /status endpoint (column validation,
// Scheduled cleanup, activity, engine signal after commit). This is the hand-off
// primitive: {"status":"Todo","assignedTo":"programmer"} can never be observed
// half-applied by the automation engine. ExpectedStatus is optimistic concurrency:
// when set, the update only applies while the ticket is still in that status (else 409).
public record UpdateTicketRequest(string Author, string? Title = null, string? Description = null, TicketPriority? Priority = null, string? AssignedTo = null, List<int>? LabelIds = null, string? Status = null, string? ExpectedStatus = null, int? PipelineId = null, int? ColumnId = null, bool? BlocksParent = null);
public record MoveTicketRequest(string Status, string Author);
public record TransferTicketRequest(string TargetProject, string Actor);
public record ScheduleTicketRequest(DateTime FireAt, string Author, string? TargetStatus = "Todo");
// Content/Author are nullable so missing JSON fields deserialize cleanly; the service
// rejects null/whitespace with 400 rather than letting SQLite throw NOT NULL (500).
public record AddCommentRequest(string? Content, string? Author);
public record UpdateCommentRequest(string? Content, string? Author);
public record CreateLabelRequest(string Name, string Color = "#6366f1");
// Incremental ticket-label patch: merges add/remove (label NAMES, case-insensitive) into
// the ticket's current labels server-side — the safe primitive for concurrent writers.
// Unknown names in Add are created on the fly; unknown names in Remove are ignored.
public record PatchTicketLabelsRequest(string Author, List<string>? Add = null, List<string>? Remove = null);
public record UpdateLabelRequest(string? Name = null, string? Color = null);
public record SetTicketLabelsRequest(List<int> LabelIds);
public record ReorderTicketRequest(string Status, int Index);
public record CreateColumnRequest(string Name, string Color = "#5a6a80", int? PipelineId = null, ColumnRole Role = ColumnRole.Normal);
public record UpdateColumnRequest(string? Name = null, string? Color = null, ColumnRole? Role = null);
public record ReorderColumnRequest(int ColumnId, int Index);
public record CreateMemberRequest(string Name);
public record UpdateMemberRequest(string? Name = null, string? DefaultModel = null);
public record SetParentRequest(int ParentId);
public record UpdateProjectRequest(string? WorkspacePath = null, string? FallbackModel = null, bool UpdateFallbackModel = false);
public record SaveLocalModelConfigRequest(string? LocalModelBaseUrl = null, string? LocalModelName = null);
public record SteerRunRequest(string Text);
public record BrowseFolderRequest(string? InitialPath = null);
public record ChatImageDto(string DataUrl, string Mime, string Name, long SizeBytes);
public record ChatStartRequest(string Message, string Target = "owner-chat", bool ForceNew = false, int? TicketId = null, IReadOnlyList<ChatImageDto>? Images = null, string? Model = null);
public record ChatTargetDto(string Slug, string Name, string Kind);
public record ChatTargetsResponse(string? LastTarget, List<ChatTargetDto> Targets);
public record ChatMessageDto(string Role, string Text, string? ToolName, string? Detail, string CreatedAt);
public record MoveTileRequest(int X, int Y); // required — pixel coords snapped to 20px grid
public record ResizeTileRequest(int Width, int Height); // required — pixels snapped to 20px grid
