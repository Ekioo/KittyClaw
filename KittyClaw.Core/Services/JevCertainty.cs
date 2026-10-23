namespace KittyClaw.Core.Services;

/// <summary>Only usable, sufficiently confident judgments may replace classic processing.</summary>
public static class JevCertainty
{
    public static string Configuration(KittyClaw.Core.Models.ColumnProcessor processor) => JevWorkReviewService.Hash(new
    {
        processor.Id, processor.ColumnId, processor.Name, processor.Enabled, processor.Mission, processor.Prompt, processor.Model,
        processor.JevObservationOnly, processor.JevAcceptanceValidation, processor.JevExecutionSelection, processor.JevConfidenceThreshold,
        processor.JevAcceptanceCriteria, processor.JevExecutionProfiles, processor.AvailableSkills, processor.RecommendedSkills, processor.RequiredSkills,
        processor.Routes, processor.DefaultTargetColumnId, processor.TechnicalFailureColumnId, processor.BeforeActions, processor.AfterActions
    });
    private static bool Meets(double? confidence, double threshold) => confidence is double value &&
        double.IsFinite(value) && double.IsFinite(threshold) && value >= threshold;

    public static bool Routing(JevRoutingProposal? proposal, double threshold) => proposal is { Error: null } &&
        proposal.Choice?.StartsWith("column-", StringComparison.Ordinal) == true && Meets(proposal.Confidence, threshold);

    public static bool Work(JevWorkReview? review, double threshold) => review is { Error: null } &&
        (review.Kind == "profile"
            ? review.Profile is not null && Meets(review.Confidence, threshold)
            : review.Criteria.Count > 0 && review.Criteria.All(c => c.Status is "satisfied" or "unmet" && Meets(c.Confidence, threshold)));
}
