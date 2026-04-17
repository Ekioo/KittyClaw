using Todo.Core.Services;

namespace Todo.Core.Automation.Triggers;

/// <summary>
/// A trigger decides when an automation's actions fire, and provides the context
/// (ticket id, title, status) for each firing.
/// </summary>
public interface ITrigger
{
    /// <summary>
    /// Evaluate the trigger against current project state.
    /// Return one entry per dispatch that should happen this tick.
    /// </summary>
    Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct);
}

public sealed class TriggerContext
{
    public required string ProjectSlug { get; init; }
    public required string WorkspacePath { get; init; }
    public required Automation Automation { get; init; }
    public required TicketService Tickets { get; init; }
    public required MemberService Members { get; init; }
    public required SessionRegistry Sessions { get; init; }
    public required AgentRunRegistry Runs { get; init; }
    public required DateTime Now { get; init; }
}

public sealed record TriggerFiring(int? TicketId, string? TicketTitle, string? TicketStatus);
