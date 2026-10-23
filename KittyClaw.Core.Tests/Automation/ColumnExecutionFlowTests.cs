using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnExecutionFlowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Transition_and_finalization_have_one_order_for_each_routing_policy(bool guarded)
    {
        var stages = new List<ColumnExecutionStage>();
        var stage = ColumnExecutionStage.Preparation;
        for (var i = 0; stage != ColumnExecutionStage.Stopped && i < 20; i++)
        {
            stages.Add(stage);
            stage = ColumnExecutionFlow.Next(stage, guarded);
        }
        Assert.Equal(ColumnExecutionStage.Stopped, stage);
        Assert.Equal(stages.Count, stages.Distinct().Count());
        Assert.True(stages.IndexOf(ColumnExecutionStage.Result) < stages.IndexOf(ColumnExecutionStage.RoutingAnalysis));
        Assert.True(stages.IndexOf(ColumnExecutionStage.RoutingValidation) < stages.IndexOf(ColumnExecutionStage.Transition));
        Assert.Equal(guarded, stages.IndexOf(ColumnExecutionStage.Transition) < stages.IndexOf(ColumnExecutionStage.Finalization));
        Assert.Equal(!guarded, stages.Contains(ColumnExecutionStage.DeferredPreparation));
    }

    [Fact]
    public void Failure_or_hold_stops_every_stage_without_entering_a_later_stage()
    {
        foreach (var stage in Enum.GetValues<ColumnExecutionStage>())
        foreach (var guarded in new[] { true, false })
            Assert.Equal(ColumnExecutionStage.Stopped, ColumnExecutionFlow.Next(stage, guarded, proceed: false));
    }

    [Theory]
    [InlineData(ColumnProcessingMode.None, false, false, false, ColumnWorkState.Disabled)]
    [InlineData(ColumnProcessingMode.None, true, true, true, ColumnWorkState.Disabled)]
    [InlineData(ColumnProcessingMode.Triage, false, false, false, ColumnWorkState.AwaitingRouting)]
    [InlineData(ColumnProcessingMode.Triage, false, true, false, ColumnWorkState.AwaitingRouting)]
    [InlineData(ColumnProcessingMode.Triage, true, true, false, ColumnWorkState.ReadyForAgent)]
    [InlineData(ColumnProcessingMode.Review, false, false, false, ColumnWorkState.AwaitingOwnerReview)]
    [InlineData(ColumnProcessingMode.Review, false, true, false, ColumnWorkState.AwaitingOwnerReview)]
    [InlineData(ColumnProcessingMode.Review, true, true, false, ColumnWorkState.ReadyForAgent)]
    [InlineData(ColumnProcessingMode.Agent, false, false, false, ColumnWorkState.AwaitingProfileApproval)]
    [InlineData(ColumnProcessingMode.Agent, false, false, true, ColumnWorkState.ReadyForAgent)]
    [InlineData(ColumnProcessingMode.Agent, false, true, false, ColumnWorkState.ReadyForAgent)]
    public void JEV_analysis_and_owner_decisions_gate_new_agent_claims(ColumnProcessingMode mode,
        bool allowFallback, bool fallback, bool approved, object expected)
    {
        Assert.Equal((ColumnWorkState)expected, ColumnWorkFlow.Resolve(new(mode, allowFallback, SelectProfile: true), fallback, approved));
    }
}
