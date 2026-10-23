namespace KittyClaw.Core.Models;

public enum ColumnProcessingMode { None, Agent, Triage, Review }
public enum ColumnProcessingStage
{
    Definition, Preparation, ProfileSelection, Execution, Result, JevAnalysis,
    ReviewCriteria, OwnerRouting, Routing, Finalization, Recovery
}

/// <summary>The configuration state determines which ordered stages can run or be edited.</summary>
public sealed record ColumnProcessingState(
    ColumnProcessingMode Mode = ColumnProcessingMode.None,
    bool FallbackToAgent = false,
    bool SelectProfile = false,
    bool RouteWithJev = false)
{
    public static ColumnProcessingState From(ColumnProcessor? processor) => processor is null ? new() : new(
        processor switch
        {
            { Enabled: false } => ColumnProcessingMode.None,
            { JevAcceptanceValidation: true } => ColumnProcessingMode.Review,
            { JevObservationOnly: true } => ColumnProcessingMode.Triage,
            _ => ColumnProcessingMode.Agent
        }, processor.JevFallbackToAgent, processor.JevExecutionSelection, processor.JevAutomaticRouting);

    public ColumnProcessingState TransitionTo(ColumnProcessingMode mode) => this with { Mode = mode };
    public bool IsJevOnly => Mode is ColumnProcessingMode.Triage or ColumnProcessingMode.Review;
    public bool RunsAgent => Mode == ColumnProcessingMode.Agent || IsJevOnly && FallbackToAgent;
    public bool RequestsAutomaticJev => Mode == ColumnProcessingMode.Triage;

    public IReadOnlyList<ColumnProcessingStage> Stages => Mode switch
    {
        ColumnProcessingMode.None => [],
        ColumnProcessingMode.Agent => AgentStages,
        ColumnProcessingMode.Triage => JevStages,
        ColumnProcessingMode.Review => JevStages,
        _ => throw new ArgumentOutOfRangeException(nameof(Mode))
    };

    private IReadOnlyList<ColumnProcessingStage> AgentStages => SelectProfile
        ? [ColumnProcessingStage.Definition, ColumnProcessingStage.Preparation, ColumnProcessingStage.ProfileSelection,
           ColumnProcessingStage.Execution, ColumnProcessingStage.Result, ColumnProcessingStage.Routing,
           ColumnProcessingStage.Finalization, ColumnProcessingStage.Recovery]
        : [ColumnProcessingStage.Definition, ColumnProcessingStage.Preparation, ColumnProcessingStage.Execution,
           ColumnProcessingStage.Result, ColumnProcessingStage.Routing, ColumnProcessingStage.Finalization,
           ColumnProcessingStage.Recovery];

    private IReadOnlyList<ColumnProcessingStage> JevStages
    {
        get
        {
            List<ColumnProcessingStage> stages = [ColumnProcessingStage.Definition, ColumnProcessingStage.JevAnalysis];
            if (Mode == ColumnProcessingMode.Review) stages.Add(ColumnProcessingStage.ReviewCriteria);
            if (FallbackToAgent) stages.AddRange([ColumnProcessingStage.Preparation, ColumnProcessingStage.Execution, ColumnProcessingStage.Result]);
            if (FallbackToAgent && Mode == ColumnProcessingMode.Review) stages.Add(ColumnProcessingStage.Routing);
            stages.Add(Mode == ColumnProcessingMode.Review ? ColumnProcessingStage.OwnerRouting : ColumnProcessingStage.Routing);
            if (FallbackToAgent) stages.AddRange([ColumnProcessingStage.Finalization, ColumnProcessingStage.Recovery]);
            return stages;
        }
    }
}
