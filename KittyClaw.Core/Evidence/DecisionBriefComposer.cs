namespace KittyClaw.Core.Evidence;

/// <summary>
/// Derives a <see cref="TicketDecisionBrief"/> from a <see cref="TicketEvidence"/> bundle.
/// No transcript parsing is performed; all inputs come from the structured evidence contract.
/// </summary>
public static class DecisionBriefComposer
{
    public static TicketDecisionBrief Compose(TicketEvidence evidence)
    {
        var findings = CollectFindings(evidence);
        var verdict = ComputeVerdict(evidence, findings);

        int testsPassed = evidence.CommandsRun.Sum(c => c.TestsPassed ?? 0);
        int testsFailed = evidence.CommandsRun.Sum(c => c.TestsFailed ?? 0);

        return new TicketDecisionBrief
        {
            TicketId = evidence.TicketId,
            ProjectSlug = evidence.ProjectSlug,
            CapturedAt = evidence.CapturedAt,
            Verdict = verdict,
            EvidenceStatus = evidence.Status,
            FilesChanged = evidence.ChangedFiles.Count,
            CommandsRun = evidence.CommandsRun.Count,
            TestsPassed = testsPassed,
            TestsFailed = testsFailed,
            RepositoryClean = evidence.RepositoryState?.IsClean,
            RunIds = new List<string>(evidence.RunIds),
            Findings = findings,
            RecoveryGuidance = EvidenceRecoveryAdvisor.Advise(evidence),
        };
    }

    private static List<DecisionFinding> CollectFindings(TicketEvidence evidence)
    {
        var findings = new List<DecisionFinding>();

        switch (evidence.Status)
        {
            case EvidenceStatus.Missing:
                findings.Add(new DecisionFinding("missing-evidence",
                    "Evidence bundle is absent or all items are missing", IsBlocking: true));
                break;
            case EvidenceStatus.Stale:
                findings.Add(new DecisionFinding("stale-evidence",
                    "Evidence is stale and may not reflect the current state", IsBlocking: true));
                break;
            case EvidenceStatus.Contradictory:
                findings.Add(new DecisionFinding("contradictory-evidence",
                    "Verified items from different sources disagree on the same observable fact", IsBlocking: true));
                break;
        }

        foreach (var cmd in evidence.CommandsRun.Where(c => !c.Success))
            findings.Add(new DecisionFinding("command-failure",
                $"Command failed: {cmd.Command}", IsBlocking: true));

        int agentClaimCount = evidence.CommandsRun.Count(c => c.Provenance.Trust == EvidenceTrust.AgentClaim)
            + evidence.ChangedFiles.Count(f => f.Provenance.Trust == EvidenceTrust.AgentClaim);
        if (agentClaimCount > 0)
            findings.Add(new DecisionFinding("agent-claim",
                $"{agentClaimCount} item(s) carry unverified agent claims and cannot stand as verified evidence",
                IsBlocking: false));

        return findings;
    }

    private static DecisionVerdict ComputeVerdict(TicketEvidence evidence, List<DecisionFinding> findings)
    {
        // Blocking status: evidence is structurally unusable for a decision.
        if (evidence.Status is EvidenceStatus.Missing or EvidenceStatus.Stale or EvidenceStatus.Contradictory)
            return DecisionVerdict.Block;

        // Blocking findings: evidence is readable but failures require remediation.
        if (findings.Any(f => f.IsBlocking))
            return DecisionVerdict.Fix;

        return DecisionVerdict.Ship;
    }
}
