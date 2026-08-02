using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace KittyClaw.Core.Models;

public enum ScheduledTaskTicketScope
{
    None,
    FirstTicket,
}

public enum ColumnScheduledTaskRunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A cron task owned by a stable column and defined in the project workspace.</summary>
public sealed class ColumnScheduledTask
{
    public required string Id { get; set; }
    public int ColumnId { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public string Cron { get; set; } = "0 9 * * 1";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public ScheduledTaskTicketScope TicketScope { get; set; }
    public string ActionsJson { get; set; } = "[]";
    public DateTime NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public List<ColumnProcessorAction> Actions
    {
        get => JsonSerializer.Deserialize<List<ColumnProcessorAction>>(ActionsJson) ?? [];
        set => ActionsJson = JsonSerializer.Serialize(value);
    }
}

/// <summary>Durable scheduled-task run with per-action checkpoints.</summary>
public sealed class ColumnScheduledTaskRun
{
    public required string Id { get; set; }
    public required string TaskId { get; set; }
    public ColumnScheduledTaskRunStatus Status { get; set; } = ColumnScheduledTaskRunStatus.Running;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public string CompletedActionIdsJson { get; set; } = "[]";
    public string? CurrentActionId { get; set; }
    public string? Error { get; set; }

    [NotMapped]
    public List<string> CompletedActionIds
    {
        get => JsonSerializer.Deserialize<List<string>>(CompletedActionIdsJson) ?? [];
        set => CompletedActionIdsJson = JsonSerializer.Serialize(value
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
