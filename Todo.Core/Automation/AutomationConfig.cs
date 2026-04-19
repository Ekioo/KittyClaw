using System.Text.Json.Serialization;

namespace Todo.Core.Automation;

public sealed class AutomationConfig
{
    public List<Automation> Automations { get; set; } = new();
    public decimal? DailyBudgetUsd { get; set; }
    public int? MinDescriptionLength { get; set; }
}

public sealed class Automation
{
    public required string Id { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required TriggerSpec Trigger { get; set; }
    public List<ConditionSpec> Conditions { get; set; } = new();
    public List<ActionSpec> Actions { get; set; } = new();
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
public abstract class TriggerSpec { }

public sealed class IntervalTriggerSpec : TriggerSpec
{
    public int? Seconds { get; set; }
    public string? Cron { get; set; }
}

public sealed class TicketInColumnTriggerSpec : TriggerSpec
{
    public int Seconds { get; set; } = 30;
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
    public int DebounceSeconds { get; set; } = 0;
}

public sealed class GitCommitTriggerSpec : TriggerSpec
{
    public int PollSeconds { get; set; } = 60;
    public List<string> IgnoreAuthors { get; set; } = new() { "noreply@anthropic.com" };
}

public sealed class StatusChangeTriggerSpec : TriggerSpec
{
    public int PollSeconds { get; set; } = 30;
    public string? From { get; set; }
    public string? To { get; set; }
    public int? DebounceSeconds { get; set; }
}

public sealed class SubTicketStatusTriggerSpec : TriggerSpec
{
    public int PollSeconds { get; set; } = 30;
    public string? ParentColumn { get; set; }
    public int? DebounceSeconds { get; set; }
}

public sealed class BoardIdleTriggerSpec : TriggerSpec
{
    public int PollSeconds { get; set; } = 60;
    public List<string> IdleColumns { get; set; } = new() { "Done", "Review" };
}

public sealed class AgentInactivityTriggerSpec : TriggerSpec
{
    public int PollSeconds { get; set; } = 60;
    public int MinutesIdle { get; set; } = 45;
}

public sealed class TicketCommentAddedTriggerSpec : TriggerSpec
{
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
    /// <summary>When true, the condition result is inverted (NOT logic).</summary>
    public bool Negate { get; set; }
}

public sealed class TicketInColumnConditionSpec : ConditionSpec
{
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
}

/// <summary>Kept for backward-compat with existing automations.json files.</summary>
public sealed class MinDescriptionLengthConditionSpec : ConditionSpec
{
    public int Length { get; set; } = 50;
}

public sealed class FieldLengthConditionSpec : ConditionSpec
{
    /// <summary>"title" or "description"</summary>
    public string Field { get; set; } = "description";
    /// <summary>"min" or "max"</summary>
    public string Mode { get; set; } = "min";
    public int Length { get; set; } = 50;
}

public sealed class PriorityConditionSpec : ConditionSpec
{
    public List<string> Priorities { get; set; } = new();
}

public sealed class LabelsConditionSpec : ConditionSpec
{
    /// <summary>Ticket must have at least one of these labels.</summary>
    public List<string> Labels { get; set; } = new();
}

public sealed class AssignedToConditionSpec : ConditionSpec
{
    /// <summary>Matches if ticket is assigned to one of these slugs. Empty = unassigned.</summary>
    public List<string> Slugs { get; set; } = new();
}

public sealed class HasParentConditionSpec : ConditionSpec
{
    /// <summary>true = ticket must have a parent; false = ticket must be a root ticket.</summary>
    public bool Value { get; set; }
}

/// <summary>
/// Matches if the firing ticket has sub-tickets AND every sub-ticket's status is in <see cref="Statuses"/>.
/// A ticket with zero sub-tickets does NOT match (safer default — otherwise every leaf ticket would match).
/// </summary>
public sealed class AllSubTicketsInStatusConditionSpec : ConditionSpec
{
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
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
    public bool SameAssignee { get; set; }
    /// <summary>One of "==", "!=", "&lt;", "&lt;=", "&gt;", "&gt;=".</summary>
    public string Operator { get; set; } = "==";
    public int Value { get; set; }
}

public sealed class TicketAgeConditionSpec : ConditionSpec
{
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
[JsonDerivedType(typeof(ExecutePowerShellActionSpec), "executePowerShell")]
public abstract class ActionSpec { }

public sealed class RunAgentActionSpec : ActionSpec
{
    /// <summary>
    /// Name of the agent to run. Must match a member slug in the project.
    /// Resolved to <c>.agents/{Agent}/SKILL.md</c> at dispatch time.
    /// </summary>
    public required string Agent { get; set; }
    public int MaxTurns { get; set; } = 200;
    public string? ConcurrencyGroup { get; set; }
    public List<string> MutuallyExclusiveWith { get; set; } = new();
    public string? Context { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Model { get; set; }
    public bool RestoreStatusOnFail { get; set; } = true;
}

public sealed class MoveTicketStatusActionSpec : ActionSpec
{
    public required string To { get; set; }
}

public sealed class SetLabelsActionSpec : ActionSpec
{
    /// <summary>Label names to add to the ticket.</summary>
    public List<string> Add { get; set; } = new();
    /// <summary>Label names to remove from the ticket.</summary>
    public List<string> Remove { get; set; } = new();
}

public sealed class AssignTicketActionSpec : ActionSpec
{
    /// <summary>Member slug to assign. Empty or null to unassign. Supports {previousAssignee} placeholder.</summary>
    public string? Slug { get; set; }
}

public sealed class AddCommentActionSpec : ActionSpec
{
    /// <summary>Comment content. Supports placeholders: {ticketId}, {ticketTitle}, {assignee}.</summary>
    public string Content { get; set; } = "";
    /// <summary>Author of the comment (member slug).</summary>
    public string Author { get; set; } = "";
}

/// <summary>Persists the given agent's memory.md (touch / flush). No-op placeholder for now.</summary>
public sealed class CommitAgentMemoryActionSpec : ActionSpec
{
    public required string Agent { get; set; }
}

/// <summary>Runs a PowerShell script or file with optional arguments and timeout.</summary>
public sealed class ExecutePowerShellActionSpec : ActionSpec
{
    public string Script { get; set; } = "";
    public string? ScriptFile { get; set; }
    public List<string> Arguments { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 60;
    public bool AbortOnFailure { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
}
