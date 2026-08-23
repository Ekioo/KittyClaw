using KittyClaw.Core.Evidence;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Components;

public static class DecisionBriefDiagnosticLocalizer
{
    public static string Finding(LocalizationService localization, DecisionFinding finding)
    {
        return finding.Category switch
        {
            "missing-evidence" => localization["BriefFindingMissingEvidence"],
            "stale-evidence" => localization["BriefFindingStaleEvidence"],
            "contradictory-evidence" => localization["BriefFindingContradictoryEvidence"],
            "command-failure" => localization.Get("BriefFindingCommandFailure", DetailAfter(finding.Summary, ':')),
            "agent-claim" => localization.Get("BriefFindingAgentClaim", FirstToken(finding.Summary)),
            _ => finding.Summary,
        };
    }

    public static string Recovery(LocalizationService localization, TicketDecisionBrief brief)
    {
        return brief.EvidenceStatus switch
        {
            EvidenceStatus.Missing => localization["BriefRecoveryMissingEvidence"],
            EvidenceStatus.Stale => localization["BriefRecoveryStaleEvidence"],
            EvidenceStatus.Contradictory => localization.Get(
                "BriefRecoveryContradictoryEvidence", ContradictoryPaths(localization, brief)),
            EvidenceStatus.Partial => localization["BriefRecoveryPartialEvidence"],
            _ => brief.RecoveryGuidance.Reason,
        };
    }

    private static string DetailAfter(string value, char separator)
    {
        var index = value.IndexOf(separator);
        return index >= 0 ? value[(index + 1)..].Trim() : value;
    }

    private static string FirstToken(string value)
    {
        var index = value.IndexOf(' ');
        return index >= 0 ? value[..index] : value;
    }

    private static string ContradictoryPaths(LocalizationService localization, TicketDecisionBrief brief)
    {
        var paths = brief.ChangedFiles
            .Where(file => file.Provenance.Trust == EvidenceTrust.Verified)
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(file => file.Kind).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToList();

        return paths.Count > 0 ? string.Join(", ", paths) : localization["BriefUnknownPaths"];
    }
}
