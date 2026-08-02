using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.Core.Automation;

public sealed class AutomationConfig
{
    public List<Automation> Automations { get; set; } = new();
    public decimal? DailyBudgetUsd { get; set; }
    public int? MinDescriptionLength { get; set; }

    /// <summary>Round-trips fields not modeled here (agents may annotate automations.json with
    /// custom keys); without this they would be silently dropped on save (ticket #115).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class Automation
{
    public required string Id { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required TriggerSpec Trigger { get; set; }
    public List<ConditionSpec> Conditions { get; set; } = new();
    public List<ActionSpec> Actions { get; set; } = new();

    /// <summary>Round-trips unknown per-automation fields (e.g. custom pins added by agents).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IntervalTriggerSpec), "interval")]
[JsonDerivedType(typeof(TicketInColumnTriggerSpec), "ticketInColumn")]
[JsonDerivedType(typeof(GitCommitTriggerSpec), "gitCommit")]
[JsonDerivedType(typeof(StatusChangeTriggerSpec), "statusChange")]
[JsonDerivedType(typeof(SubTicketStatusTriggerSpec), "subTicketStatus")]
[JsonDerivedType(typeof(BoardIdleTriggerSpec), "boardIdle")]
[JsonDerivedType(typeof(AgentInactivityTriggerSpec), "agentInactivity")]
[JsonDerivedType(typeof(TicketCommentAddedTriggerSpec), "ticketCommentAdded")]
public abstract class TriggerSpec
{
    [JsonIgnore]
    public abstract string UiTypeKey { get; }

    /// <summary>Round-trips unknown trigger fields so a save never strips hand-added keys.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class IntervalTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "interval";
    public string? Cron { get; set; }
    /// <summary>Legacy fixed-interval seconds, pre-dating the cron-only model. Converted to an
    /// equivalent cron expression at trigger-build time if <see cref="Cron"/> is unset (see
    /// <c>IntervalTrigger.SecondsToCron</c>). The trigger editor UI no longer writes this field —
    /// new automations should always set <see cref="Cron"/>.</summary>
    public int? Seconds { get; set; }
}

public sealed class TicketInColumnTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "ticketInColumn";
    public int Seconds { get; set; } = 30;
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
    public int DebounceSeconds { get; set; } = 0;
    public int MaxConsecutiveRuns { get; set; } = 5;
    public int FailureBackoffSeconds { get; set; } = 60;
    public int MaxFailureBackoffSeconds { get; set; } = 1800;
}

public sealed class GitCommitTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "gitCommit";
    public int PollSeconds { get; set; } = 60;
    public List<string> IgnoreAuthors { get; set; } = new() { "noreply@anthropic.com" };
}

public sealed class StatusChangeTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "statusChange";
    public int PollSeconds { get; set; } = 30;
    public string? From { get; set; }
    public string? To { get; set; }
    public int? DebounceSeconds { get; set; }
}

public sealed class SubTicketStatusTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "subTicketStatus";
    public int PollSeconds { get; set; } = 30;
    public string? ParentColumn { get; set; }
    public int? DebounceSeconds { get; set; }
}

public sealed class BoardIdleTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "boardIdle";
    public int PollSeconds { get; set; } = 60;
    public List<string> IdleColumns { get; set; } = new() { "Done", "Review" };
}

public sealed class AgentInactivityTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "agentInactivity";
    public int PollSeconds { get; set; } = 60;
    public int MinutesIdle { get; set; } = 45;
}

public sealed class TicketCommentAddedTriggerSpec : TriggerSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "ticketCommentAdded";
    public int PollSeconds { get; set; } = 30;
    public List<string> Authors { get; set; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TicketInColumnConditionSpec), "ticketInColumn")]
[JsonDerivedType(typeof(MinDescriptionLengthConditionSpec), "minDescriptionLength")]
[JsonDerivedType(typeof(FieldLengthConditionSpec), "fieldLength")]
[JsonDerivedType(typeof(PriorityConditionSpec), "priority")]
[JsonDerivedType(typeof(LabelsConditionSpec), "labels")]
[JsonDerivedType(typeof(AssignedToConditionSpec), "assignedTo")]
[JsonDerivedType(typeof(TicketAgeConditionSpec), "ticketAge")]
[JsonDerivedType(typeof(HasParentConditionSpec), "hasParent")]
[JsonDerivedType(typeof(AllSubTicketsInStatusConditionSpec), "allSubTicketsInStatus")]
[JsonDerivedType(typeof(TicketCountInColumnConditionSpec), "ticketCountInColumn")]
public abstract class ConditionSpec
{
    [JsonIgnore]
    public abstract string UiTypeKey { get; }
    /// <summary>When true, the condition result is inverted (NOT logic).</summary>
    public bool Negate { get; set; }

    /// <summary>Round-trips unknown condition fields so a save never strips hand-added keys.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TicketInColumnConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "ticketInColumn";
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
}

/// <summary>Kept for backward-compat with existing automations.json files.</summary>
public sealed class MinDescriptionLengthConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "minDescriptionLength";
    public int Length { get; set; } = 50;
}

public sealed class FieldLengthConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "fieldLength";
    /// <summary>"title" or "description"</summary>
    public string Field { get; set; } = "description";
    /// <summary>"min" or "max"</summary>
    public string Mode { get; set; } = "min";
    public int Length { get; set; } = 50;
}

public sealed class PriorityConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "priority";
    public List<string> Priorities { get; set; } = new();
}

public sealed class LabelsConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "labels";
    /// <summary>Ticket must have at least one of these labels.</summary>
    public List<string> Labels { get; set; } = new();
}

public sealed class AssignedToConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "assignedTo";
    /// <summary>Matches if ticket is assigned to one of these slugs. Empty = unassigned.</summary>
    public List<string> Slugs { get; set; } = new();
}

public sealed class HasParentConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "hasParent";
    /// <summary>true = ticket must have a parent; false = ticket must be a root ticket.</summary>
    public bool Value { get; set; }
}

/// <summary>
/// Matches if the firing ticket has sub-tickets AND every sub-ticket's status is in <see cref="Statuses"/>.
/// A ticket with zero sub-tickets does NOT match (safer default — otherwise every leaf ticket would match).
/// </summary>
public sealed class AllSubTicketsInStatusConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "allSubTicketsInStatus";
    public List<string> Statuses { get; set; } = new() { "Done" };
}

/// <summary>
/// Generic count-based condition: matches if the number of tickets assigned to a given member
/// (or the firing ticket's assignee when <see cref="SameAssignee"/>) in the listed columns
/// satisfies the operator/value comparison. Generalizes NoPendingTickets (which is
/// equivalent to Operator="==" Value=0).
/// </summary>
public sealed class TicketCountInColumnConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "ticketCountInColumn";
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
    public bool SameAssignee { get; set; }
    /// <summary>One of "==", "!=", "&lt;", "&lt;=", "&gt;", "&gt;=".</summary>
    public string Operator { get; set; } = "==";
    public int Value { get; set; }
}

public sealed class TicketAgeConditionSpec : ConditionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "ticketAge";
    /// <summary>"createdAt" or "updatedAt"</summary>
    public string Field { get; set; } = "createdAt";
    /// <summary>"olderThan" or "newerThan"</summary>
    public string Mode { get; set; } = "olderThan";
    public int Hours { get; set; } = 24;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunAgentActionSpec), "runAgent")]
[JsonDerivedType(typeof(MoveTicketStatusActionSpec), "moveTicketStatus")]
[JsonDerivedType(typeof(SetLabelsActionSpec), "setLabels")]
[JsonDerivedType(typeof(AssignTicketActionSpec), "assignTicket")]
[JsonDerivedType(typeof(AddCommentActionSpec), "addComment")]
[JsonDerivedType(typeof(CommitAgentMemoryActionSpec), "commitAgentMemory")]
[JsonDerivedType(typeof(ConsolidateAgentMemoryActionSpec), "consolidateAgentMemory")]
[JsonDerivedType(typeof(ExecutePowerShellActionSpec), "executePowerShell")]
[JsonDerivedType(typeof(CreateTicketActionSpec), "createTicket")]
[JsonDerivedType(typeof(HttpRequestActionSpec), "httpRequest")]
public abstract class ActionSpec
{
    [JsonIgnore]
    public abstract string UiTypeKey { get; }

    /// <summary>Round-trips unknown action fields so a save never strips hand-added keys.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class RunAgentActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "runAgent";
    /// <summary>
    /// Name of the agent to run. Must match a member slug in the project.
    /// Resolved to <c>.agents/{Agent}/SKILL.md</c> at dispatch time.
    /// </summary>
    public required string Agent { get; set; }
    public int MaxTurns { get; set; } = 200;
    public string? ConcurrencyGroup { get; set; }
    /// <summary>Dead man's switch: if the run holding this concurrency group emits no activity for
    /// this many minutes, the reaper force-releases the lock. Null (default) disables the timeout.
    /// Guards against a hung subprocess that never returns nor throws (see ticket #98).</summary>
    public int? LockTimeoutMinutes { get; set; }
    public List<string> MutuallyExclusiveWith { get; set; } = new();
    public string? Context { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Model { get; set; }
    public bool RestoreStatusOnFail { get; set; } = true;
}

public sealed class MoveTicketStatusActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "moveTicketStatus";
    public required string To { get; set; }
}

public sealed class SetLabelsActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "setLabels";
    /// <summary>Label names to add to the ticket.</summary>
    public List<string> Add { get; set; } = new();
    /// <summary>Label names to remove from the ticket.</summary>
    public List<string> Remove { get; set; } = new();
}

public sealed class AssignTicketActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "assignTicket";
    /// <summary>Member slug to assign. Empty or null to unassign. Supports {previousAssignee} placeholder.</summary>
    public string? Slug { get; set; }
}

public sealed class AddCommentActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "addComment";
    /// <summary>Comment content. Supports placeholders: {ticketId}, {ticketTitle}, {assignee}.</summary>
    public string Content { get; set; } = "";
    /// <summary>Author of the comment (member slug).</summary>
    public string Author { get; set; } = "";
}

/// <summary>Git-commits the given agent's memory (the .agents/{agent}/memory/ topic layout and/or
/// the legacy flat memory.md) after a run.</summary>
public sealed class CommitAgentMemoryActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "commitAgentMemory";
    public required string Agent { get; set; }
}

/// <summary>
/// Spawns a focused claude pass whose only job is to distill lessons from the parent run
/// into the agent's memory (the .agents/{agent}/memory/ topic layout). Instructions are read
/// from an external markdown file so they can be tweaked without rebuilding.
/// </summary>
public sealed class ConsolidateAgentMemoryActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "consolidateAgentMemory";
    /// <summary>Agent slug. Supports {assignee} placeholder.</summary>
    public required string Agent { get; set; }
    /// <summary>Optional model target. When omitted, member and project defaults are used.</summary>
    public string? Model { get; set; }
    /// <summary>Max turns for the consolidation pass.</summary>
    public int MaxTurns { get; set; } = 5;
    /// <summary>Path to the instruction markdown file, relative to workspace root.</summary>
    public string InstructionFile { get; set; } = ".agents/memory-consolidation.md";
}

/// <summary>
/// Creates a new ticket in the project. Works without a triggering ticket (interval, cron, board-idle, …).
/// Supports date/time placeholders in Title and Description: {now} (date + time), {date} (today), {time}, {monday} (Monday of current week), {firstOfMonth}.
/// When <see cref="SkipIfExists"/> is true (default), creation is skipped if an open ticket with the resolved title already exists.
/// </summary>
public sealed class CreateTicketActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "createTicket";
    /// <summary>Ticket title. Supports {now}, {date}, {time}, {monday}, {firstOfMonth}.</summary>
    public string Title { get; set; } = "";
    /// <summary>Ticket description (optional). Supports {now}, {date}, {time}, {monday}, {firstOfMonth}.</summary>
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Todo";
    public string? AssignedTo { get; set; }
    public string Priority { get; set; } = "NiceToHave";
    /// <summary>Label names to attach to the new ticket.</summary>
    public List<string> Labels { get; set; } = new();
    public int? ParentId { get; set; }
    public string CreatedBy { get; set; } = "automation";
    /// <summary>Skip creation if an open ticket with the same resolved title already exists.</summary>
    public bool SkipIfExists { get; set; } = true;
}

/// <summary>
/// Sends an outbound HTTP request — webhooks and integrations (notify Discord/Slack on a status
/// change, trigger a CI job, ping an external service). Loopback and link-local targets
/// (including cloud metadata endpoints) are refused unless <see cref="AllowLocalTargets"/> is
/// set, so an automation edited by an agent cannot probe the host's internal network.
/// </summary>
public sealed class HttpRequestActionSpec : ActionSpec
{
    public override string UiTypeKey => "httpRequest";
    /// <summary>One of GET, POST, PUT, PATCH, DELETE.</summary>
    public string Method { get; set; } = "POST";
    /// <summary>Target URL, http/https only. Supports {ticketId}, {ticketTitle}, {ticketStatus}, {assignee}.</summary>
    public string Url { get; set; } = "";
    /// <summary>Extra request headers. Values support the same placeholders and are never logged.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Request body (empty = none). Supports the same placeholders.</summary>
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "application/json";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Abort the remaining action chain when the request fails or returns non-2xx.</summary>
    public bool AbortOnFailure { get; set; }
    /// <summary>Opt-in: allow loopback/link-local targets (blocked by default — SSRF guard).</summary>
    public bool AllowLocalTargets { get; set; }
}

/// <summary>Runs a PowerShell script or file with optional arguments and timeout.</summary>
public sealed class ExecutePowerShellActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "executePowerShell";
    public string Script { get; set; } = "";
    public string? ScriptFile { get; set; }
    public List<string> Arguments { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 60;
    public bool AbortOnFailure { get; set; }
    /// <summary>
    /// Coalesce overlapping executions per project and automation. Use this for board-wide,
    /// idempotent scripts that scan current state instead of processing one firing payload.
    /// </summary>
    public bool CoalesceOverlapping { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
}
