using KittyClaw.Core.Models;

namespace KittyClaw.Core.Automation;

internal enum ColumnWorkState { Disabled, AwaitingRouting, AwaitingOwnerReview, AwaitingProfileApproval, ReadyForAgent }

/// <summary>Admission to a new agent execution, after entry/dependency gates and JEV analysis.</summary>
internal static class ColumnWorkFlow
{
    internal static ColumnWorkState Resolve(ColumnProcessingState configuration, bool fallback = false, bool profileApproved = false) =>
        configuration switch
        {
            { Mode: ColumnProcessingMode.None } => ColumnWorkState.Disabled,
            { IsJevOnly: true, FallbackToAgent: true } when fallback => ColumnWorkState.ReadyForAgent,
            { Mode: ColumnProcessingMode.Triage } => ColumnWorkState.AwaitingRouting,
            { Mode: ColumnProcessingMode.Review } => ColumnWorkState.AwaitingOwnerReview,
            { SelectProfile: true } when !fallback && !profileApproved => ColumnWorkState.AwaitingProfileApproval,
            _ => ColumnWorkState.ReadyForAgent
        };
}
