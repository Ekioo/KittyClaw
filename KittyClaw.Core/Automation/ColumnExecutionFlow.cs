namespace KittyClaw.Core.Automation;

internal enum ColumnExecutionStage
{
    Preparation, Agent, Result, RoutingAnalysis, RoutingValidation,
    DeferredPreparation, Finalization, Transition, Stopped
}

/// <summary>Transient orchestration; durable status and checkpoints remain owned by ColumnExecutionService.</summary>
internal static class ColumnExecutionFlow
{
    internal static ColumnExecutionStage Next(ColumnExecutionStage stage, bool transitionBeforeActions, bool proceed = true) =>
        !proceed ? ColumnExecutionStage.Stopped : stage switch
        {
            ColumnExecutionStage.Preparation => ColumnExecutionStage.Agent,
            ColumnExecutionStage.Agent => ColumnExecutionStage.Result,
            ColumnExecutionStage.Result => ColumnExecutionStage.RoutingAnalysis,
            ColumnExecutionStage.RoutingAnalysis => ColumnExecutionStage.RoutingValidation,
            ColumnExecutionStage.RoutingValidation => transitionBeforeActions
                ? ColumnExecutionStage.Transition : ColumnExecutionStage.DeferredPreparation,
            ColumnExecutionStage.DeferredPreparation => ColumnExecutionStage.Finalization,
            ColumnExecutionStage.Finalization => transitionBeforeActions
                ? ColumnExecutionStage.Stopped : ColumnExecutionStage.Transition,
            ColumnExecutionStage.Transition => transitionBeforeActions
                ? ColumnExecutionStage.Finalization : ColumnExecutionStage.Stopped,
            ColumnExecutionStage.Stopped => ColumnExecutionStage.Stopped,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
}
