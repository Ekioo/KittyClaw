using KittyClaw.Core.Models;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public sealed class ColumnProcessingStateTests
{
    [Theory]
    [InlineData(ColumnProcessingMode.None, false, false)]
    [InlineData(ColumnProcessingMode.Agent, false, true)]
    [InlineData(ColumnProcessingMode.Triage, false, false)]
    [InlineData(ColumnProcessingMode.Triage, true, true)]
    [InlineData(ColumnProcessingMode.Review, false, false)]
    [InlineData(ColumnProcessingMode.Review, true, true)]
    public void Only_active_stages_contribute_drafts(ColumnProcessingMode mode, bool fallback, bool runsAgent)
    {
        var state = new ColumnProcessingState(mode, fallback);
        var visited = new List<ColumnProcessingStage>();
        var editors = Enum.GetValues<ColumnProcessingStage>().ToDictionary(stage => stage,
            stage => (Func<SaveColumnProcessorRequest, SaveColumnProcessorRequest>)(request =>
            {
                visited.Add(stage);
                Assert.Contains(stage, state.Stages);
                return request;
            }));
        var saved = new ColumnProcessor { Name = "Saved", Mission = "Valid mission" };
        var result = ColumnProcessorEditorSave.Build(state, saved, editors)!;
        Assert.Equal(state.Stages, visited);
        Assert.Equal(runsAgent, state.RunsAgent);
        Assert.Equal(runsAgent, visited.Contains(ColumnProcessingStage.Execution));
        Assert.Equal(runsAgent, visited.Contains(ColumnProcessingStage.Recovery));
        Assert.Equal(mode != ColumnProcessingMode.None, result.Enabled);
    }

    [Fact]
    public void None_does_not_create_a_processor_or_read_invalid_drafts()
    {
        Assert.Null(ColumnProcessorEditorSave.Build(new(), null, InvalidDrafts()));
        var saved = new ColumnProcessor { Name = "Saved", Mission = "Valid", JevObservationOnly = true };
        var result = ColumnProcessorEditorSave.Build(new(), saved, InvalidDrafts())!;
        Assert.False(result.Enabled);
        Assert.Equal(saved.Mission, result.Mission);
        Assert.True(result.JevObservationOnly);
    }

    [Theory]
    [InlineData(ColumnProcessingMode.Triage)]
    [InlineData(ColumnProcessingMode.Review)]
    public void Jev_without_fallback_ignores_incomplete_actions_and_profiles(ColumnProcessingMode mode)
    {
        var state = new ColumnProcessingState(mode, SelectProfile: true);
        var editors = InvalidDrafts();
        foreach (var stage in state.Stages) editors[stage] = request => request;
        var result = ColumnProcessorEditorSave.Build(state, null, editors)!;
        Assert.False(result.JevExecutionSelection);
        Assert.False(result.JevFallbackToAgent);
        Assert.Empty(result.BeforeActions ?? []);
        Assert.Empty(result.AfterActions ?? []);
        Assert.Empty(result.JevExecutionProfiles ?? []);
        Assert.True(mode == ColumnProcessingMode.Triage ? result.JevObservationOnly : result.JevAcceptanceValidation);
    }

    [Fact]
    public void Transition_keeps_preferences_but_disabled_or_absent_processors_never_request_automatic_jev()
    {
        Assert.False(ColumnProcessingState.From(null).RequestsAutomaticJev);
        Assert.False(ColumnProcessingState.From(new ColumnProcessor
            { Name = "Paused triage", Enabled = false, JevObservationOnly = true }).RequestsAutomaticJev);
        var triage = new ColumnProcessingState(ColumnProcessingMode.Triage, true, true, true);
        Assert.True(triage.RequestsAutomaticJev);
        Assert.Equal(triage, triage.TransitionTo(ColumnProcessingMode.None).TransitionTo(ColumnProcessingMode.Triage));
        Assert.False(triage.TransitionTo(ColumnProcessingMode.Review).RequestsAutomaticJev);
    }

    private static Dictionary<ColumnProcessingStage, Func<SaveColumnProcessorRequest, SaveColumnProcessorRequest>> InvalidDrafts() =>
        Enum.GetValues<ColumnProcessingStage>().ToDictionary(stage => stage,
            _ => (Func<SaveColumnProcessorRequest, SaveColumnProcessorRequest>)(_ => throw new InvalidOperationException("Incomplete inactive draft")));

    [Fact]
    public void Owner_routing_ignores_general_route_drafts_and_preserves_saved_routes()
    {
        var state = new ColumnProcessingState(ColumnProcessingMode.Review);
        Assert.Contains(ColumnProcessingStage.OwnerRouting, state.Stages);
        Assert.DoesNotContain(ColumnProcessingStage.Routing, state.Stages);
        var routes = ColumnProcessorEditorSave.OwnerRoutes(
            [new("completed", 2), new("changes_requested", 3)], 4);
        Assert.Equal([new ColumnRoute("completed", 2), new("changes_requested", 4)], routes);
    }
}
