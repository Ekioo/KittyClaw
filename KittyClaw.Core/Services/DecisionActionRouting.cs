using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

/// <summary>
/// Resolves owner decision actions from the processor routing that handed the ticket to the
/// current OwnerAction column. Destinations are identified by stable column ids; translated or
/// renamed column labels never participate in the decision.
/// </summary>
public static class DecisionActionRouting
{
    public const string ApprovedOutcome = "approved";
    public const string ChangesRequestedOutcome = "changes_requested";
    public const string AbandonedOutcome = "abandoned";

    public static DecisionActionTargets Resolve(
        IReadOnlyCollection<BoardColumn> columns,
        IReadOnlyCollection<ColumnProcessor> processors,
        IReadOnlyCollection<ColumnExecution> executions,
        BoardColumn? currentColumn)
    {
        if (currentColumn is null)
            return new(null, null, null);

        var sourceExecution = executions
            .Where(execution => execution.Status == ColumnExecutionStatus.Completed
                                && execution.TargetColumnId == currentColumn.Id)
            .OrderByDescending(execution => execution.EndedAt ?? execution.ClaimedAt)
            .FirstOrDefault();
        var sourceProcessor = sourceExecution is null
            ? processors.FirstOrDefault(processor => processor.ColumnId == currentColumn.Id)
            : processors.FirstOrDefault(processor => processor.Id == sourceExecution.ProcessorId);

        if (sourceProcessor is null)
            return ResolveLegacyFallback(columns, currentColumn);

        BoardColumn? Route(string outcome)
        {
            var targetId = sourceProcessor.Routes.FirstOrDefault(route =>
                route.Outcome.Equals(outcome, StringComparison.OrdinalIgnoreCase))?.TargetColumnId;
            return Target(targetId);
        }

        BoardColumn? Target(int? targetId) => targetId is null || targetId == currentColumn.Id
            ? null
            : columns.FirstOrDefault(column => column.Id == targetId);

        return new(
            Route(ApprovedOutcome) ?? Target(sourceProcessor.DefaultTargetColumnId),
            Route(ChangesRequestedOutcome),
            Route(AbandonedOutcome));
    }

    private static DecisionActionTargets ResolveLegacyFallback(
        IReadOnlyCollection<BoardColumn> columns,
        BoardColumn currentColumn)
    {
        var accepted = columns
            .Where(column => column.PipelineId == currentColumn.PipelineId
                             && column.SortOrder > currentColumn.SortOrder
                             && column.Role is not ColumnRole.Waiting and not ColumnRole.OwnerAction and not ColumnRole.Failure)
            .OrderBy(column => column.SortOrder)
            .FirstOrDefault();
        var stopped = columns.FirstOrDefault(column =>
            column.PipelineId == currentColumn.PipelineId && column.Role == ColumnRole.Failure);
        return new(accepted, null, stopped);
    }
}

public sealed record DecisionActionTargets(
    BoardColumn? Accepted,
    BoardColumn? CorrectionRequested,
    BoardColumn? Stopped);
