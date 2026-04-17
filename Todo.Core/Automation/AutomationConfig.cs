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
}

public sealed class GitCommitTriggerSpec : TriggerSpec
{
    public int PollSeconds { get; set; } = 60;
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
    public List<string> IdleColumns { get; set; } = new() { "Done", "OwnerReview" };
}

public sealed class AgentInactivityTriggerSpec : TriggerSpec
{
    public int PollSeconds { get; set; } = 60;
    public int MinutesIdle { get; set; } = 45;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TicketInColumnConditionSpec), "ticketInColumn")]
[JsonDerivedType(typeof(NoPendingTicketsConditionSpec), "noPendingTickets")]
[JsonDerivedType(typeof(MinDescriptionLengthConditionSpec), "minDescriptionLength")]
public abstract class ConditionSpec { }

public sealed class TicketInColumnConditionSpec : ConditionSpec
{
    public List<string> Columns { get; set; } = new();
    public string? AssigneeSlug { get; set; }
}

public sealed class NoPendingTicketsConditionSpec : ConditionSpec
{
    public string? AssigneeSlug { get; set; }
    public List<string>? Columns { get; set; }
}

public sealed class MinDescriptionLengthConditionSpec : ConditionSpec
{
    public int Length { get; set; } = 50;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunClaudeSkillActionSpec), "runClaudeSkill")]
[JsonDerivedType(typeof(MoveTicketStatusActionSpec), "moveTicketStatus")]
public abstract class ActionSpec { }

public sealed class RunClaudeSkillActionSpec : ActionSpec
{
    public required string SkillFile { get; set; }
    public string? AgentName { get; set; }
    public int MaxTurns { get; set; } = 200;
    public string? ConcurrencyGroup { get; set; }
    public List<string> MutuallyExclusiveWith { get; set; } = new();
    public string? Context { get; set; }
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Model { get; set; }
}

public sealed class MoveTicketStatusActionSpec : ActionSpec
{
    public required string To { get; set; }
}
