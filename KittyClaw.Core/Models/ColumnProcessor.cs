using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Models;

/// <summary>Persistent generic agent profile attached to one active workflow column.</summary>
public class ColumnProcessor
{
    public int Id { get; set; }
    public int ColumnId { get; set; }
    public required string Name { get; set; }
    public string Mission { get; set; } = "Process the selected ticket and return a structured outcome.";
    public string Prompt { get; set; } = "";
    public string? Model { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxTurns { get; set; } = 100;
    public TicketSelectionOrder SelectionOrder { get; set; } = TicketSelectionOrder.Position;
    public int MaxAttempts { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 60;
    public int? DefaultTargetColumnId { get; set; }
    public int? TechnicalFailureColumnId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string AvailableSkillsJson { get; set; } = "[]";
    [JsonIgnore]
    public string RecommendedSkillsJson { get; set; } = "[]";
    [JsonIgnore]
    public string RequiredSkillsJson { get; set; } = "[]";
    [JsonIgnore]
    public string RoutesJson { get; set; } = "[]";
    [JsonIgnore]
    public string BeforeActionsJson { get; set; } = "[]";
    [JsonIgnore]
    public string AfterActionsJson { get; set; } = "[]";

    [NotMapped]
    public List<string> AvailableSkills
    {
        get => Deserialize(AvailableSkillsJson);
        set => AvailableSkillsJson = Serialize(value);
    }

    [NotMapped]
    public List<string> RecommendedSkills
    {
        get => Deserialize(RecommendedSkillsJson);
        set => RecommendedSkillsJson = Serialize(value);
    }

    [NotMapped]
    public List<string> RequiredSkills
    {
        get => Deserialize(RequiredSkillsJson);
        set => RequiredSkillsJson = Serialize(value);
    }

    [NotMapped]
    public List<ColumnRoute> Routes
    {
        get => JsonSerializer.Deserialize<List<ColumnRoute>>(RoutesJson) ?? [];
        set => RoutesJson = JsonSerializer.Serialize(value
            .Where(r => !string.IsNullOrWhiteSpace(r.Outcome))
            .Select(r => r with { Outcome = r.Outcome.Trim() })
            .DistinctBy(r => r.Outcome, StringComparer.OrdinalIgnoreCase));
    }

    [NotMapped]
    public List<ColumnProcessorAction> BeforeActions
    {
        get => DeserializeActions(BeforeActionsJson);
        set => BeforeActionsJson = SerializeActions(value);
    }

    [NotMapped]
    public List<ColumnProcessorAction> AfterActions
    {
        get => DeserializeActions(AfterActionsJson);
        set => AfterActionsJson = SerializeActions(value);
    }

    private static List<string> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static string Serialize(IEnumerable<string> values) => JsonSerializer.Serialize(
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

    private static List<ColumnProcessorAction> DeserializeActions(string json) =>
        JsonSerializer.Deserialize<List<ColumnProcessorAction>>(json) ?? [];

    private static string SerializeActions(IEnumerable<ColumnProcessorAction> values) =>
        JsonSerializer.Serialize(values);
}

public sealed record ProjectSkill(string Slug, string Name, string Description, string InstructionsPath);

public sealed record ColumnRoute(string Outcome, int TargetColumnId);

/// <summary>A deterministic action surrounding the single implicit agent of a column.</summary>
public sealed record ColumnProcessorAction(
    string Id,
    ActionSpec Action,
    int? FailureTargetColumnId = null);

public enum TicketSelectionOrder
{
    Position,
    PriorityThenPosition,
    Oldest,
    Newest,
}

public enum ColumnExecutionStatus
{
    Running,
    Retrying,
    WaitingForChildren,
    Completed,
    Failed,
    Cancelled,
}

public class ColumnExecution
{
    public required string Id { get; set; }
    public int ProcessorId { get; set; }
    public int TicketId { get; set; }
    public ColumnExecutionStatus Status { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AvailableAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? RunId { get; set; }
    public string? Outcome { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public int? TargetColumnId { get; set; }
    public string CompletedActionIdsJson { get; set; } = "[]";
    public string? CurrentActionId { get; set; }
    public bool AgentCompleted { get; set; }
    public string? AgentResultJson { get; set; }

    [NotMapped]
    public List<string> CompletedActionIds
    {
        get => JsonSerializer.Deserialize<List<string>>(CompletedActionIdsJson) ?? [];
        set => CompletedActionIdsJson = JsonSerializer.Serialize(value
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [NotMapped]
    public ColumnAgentResult? AgentResult
    {
        get => string.IsNullOrWhiteSpace(AgentResultJson)
            ? null
            : JsonSerializer.Deserialize<ColumnAgentResult>(AgentResultJson);
        set => AgentResultJson = value is null ? null : JsonSerializer.Serialize(value);
    }
}

public sealed record ColumnAgentResult(
    string Outcome,
    List<string> SkillsUsed,
    string? Summary = null,
    DateTime? FireAt = null,
    string? ScheduleTarget = null);
