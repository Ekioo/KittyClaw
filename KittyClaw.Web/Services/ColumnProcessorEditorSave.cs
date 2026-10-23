using KittyClaw.Core.Models;
using KittyClaw.Web.Api;

namespace KittyClaw.Web.Services;

/// <summary>Only active stages contribute draft values. Dormant stages keep their saved values.</summary>
internal static class ColumnProcessorEditorSave
{
    internal static SaveColumnProcessorRequest? Build(ColumnProcessingState state, ColumnProcessor? saved,
        IReadOnlyDictionary<ColumnProcessingStage, Func<SaveColumnProcessorRequest, SaveColumnProcessorRequest>> stages)
    {
        if (state.Mode == ColumnProcessingMode.None && saved is null) return null;
        var request = FromSaved(saved);
        foreach (var stage in state.Stages)
            request = stages[stage](request);

        // Dormant saved profiles must remain authorized if an active execution stage changed skills.
        if (!state.Stages.Contains(ColumnProcessingStage.ProfileSelection))
        {
            var allowed = (request.AvailableSkills ?? []).Concat(request.RecommendedSkills ?? [])
                .Concat(request.RequiredSkills ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            request = request with { JevExecutionProfiles = (request.JevExecutionProfiles ?? [])
                .Select(p => p with { Skills = p.Skills.Where(allowed.Contains).ToList() }).ToList() };
        }

        return state.Mode switch
        {
            ColumnProcessingMode.None => request with { Enabled = false },
            ColumnProcessingMode.Agent => request with { Enabled = true, JevObservationOnly = false,
                JevAcceptanceValidation = false, JevExecutionSelection = state.SelectProfile,
                JevAutomaticRouting = state.RouteWithJev, JevFallbackToAgent = state.FallbackToAgent },
            ColumnProcessingMode.Triage => JevOnly(request, state) with { JevObservationOnly = true },
            ColumnProcessingMode.Review => JevOnly(request, state) with { JevAcceptanceValidation = true },
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    internal static List<ColumnRoute> OwnerRoutes(IReadOnlyList<ColumnRoute>? saved, int? correctionsTarget) =>
        (saved ?? []).Where(route => !route.Outcome.Equals("changes_requested", StringComparison.OrdinalIgnoreCase))
            .Concat(correctionsTarget is > 0 ? [new ColumnRoute("changes_requested", correctionsTarget.Value)] : []).ToList();

    private static SaveColumnProcessorRequest JevOnly(SaveColumnProcessorRequest request, ColumnProcessingState state) =>
        request with { Enabled = true, JevObservationOnly = false, JevAcceptanceValidation = false,
            JevExecutionSelection = false, JevAutomaticRouting = false, JevPostRunObservation = false,
            JevFallbackToAgent = state.FallbackToAgent };

    private static SaveColumnProcessorRequest FromSaved(ColumnProcessor? saved) => saved is null
        ? new("Column processor", "Process the selected ticket and return a structured outcome.", JevFallbackToAgent: false)
        : new(saved.Name, saved.Mission, saved.Prompt, saved.Model, saved.Enabled, saved.MaxTurns,
            saved.AvailableSkills, saved.RecommendedSkills, saved.RequiredSkills, saved.SelectionOrder,
            saved.MaxAttempts, saved.RetryBackoffSeconds, saved.DefaultTargetColumnId, saved.TechnicalFailureColumnId,
            saved.Routes, saved.BeforeActions, saved.AfterActions, saved.JevObservationOnly, saved.JevPostRunObservation,
            saved.JevAutomaticRouting, saved.JevConfidenceThreshold, saved.JevAcceptanceValidation,
            saved.JevExecutionSelection, saved.JevAcceptanceCriteria, saved.JevExecutionProfiles,
            saved.EvaluateAcceptanceCriteria, saved.JevFallbackToAgent);
}
