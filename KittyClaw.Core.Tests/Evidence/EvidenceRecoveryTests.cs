using KittyClaw.Core.Evidence;

namespace KittyClaw.Core.Tests.Evidence;

/// <summary>
/// Covers EvidenceRecoveryAdvisor — every EvidenceStatus maps to the correct RecoveryAction,
/// and the Reason + AffectedRunIds are populated explicitly rather than silently omitted.
/// </summary>
public sealed class EvidenceRecoveryTests
{
    private static EvidenceProvenance Prov(EvidenceTrust trust = EvidenceTrust.Verified,
        string runId = "run-1", string source = "process-exit") =>
        new(source, "claude", runId, DateTime.UtcNow, trust);

    private static TicketEvidence MakeBundle(EvidenceStatus status, string runId = "run-1")
    {
        var e = new TicketEvidence
        {
            TicketId = "99",
            ProjectSlug = "test",
            CapturedAt = DateTime.UtcNow,
            Status = status,
        };
        e.RunIds.Add(runId);
        return e;
    }

    // ── Missing ───────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_ReturnsRecapture()
    {
        var guidance = EvidenceRecoveryAdvisor.Advise(MakeBundle(EvidenceStatus.Missing));

        Assert.Equal(RecoveryAction.Recapture, guidance.Action);
        Assert.Contains("Re-run", guidance.Reason);
    }

    [Fact]
    public void Missing_AffectedRunIds_Listed()
    {
        var e = MakeBundle(EvidenceStatus.Missing, "run-missing");
        var guidance = EvidenceRecoveryAdvisor.Advise(e);

        Assert.Contains("run-missing", guidance.AffectedRunIds);
    }

    // ── Stale ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Stale_ReturnsRecapture()
    {
        var guidance = EvidenceRecoveryAdvisor.Advise(MakeBundle(EvidenceStatus.Stale));

        Assert.Equal(RecoveryAction.Recapture, guidance.Action);
        Assert.Contains("staleness threshold", guidance.Reason);
    }

    [Fact]
    public void Stale_AffectedRunIds_Listed()
    {
        var e = MakeBundle(EvidenceStatus.Stale, "run-old");
        var guidance = EvidenceRecoveryAdvisor.Advise(e);

        Assert.Contains("run-old", guidance.AffectedRunIds);
    }

    // ── Contradictory ─────────────────────────────────────────────────────────

    [Fact]
    public void Contradictory_ReturnsReconcile()
    {
        var guidance = EvidenceRecoveryAdvisor.Advise(MakeBundle(EvidenceStatus.Contradictory));

        Assert.Equal(RecoveryAction.Reconcile, guidance.Action);
    }

    [Fact]
    public void Contradictory_ReasonContainsConflictedPath()
    {
        var e = MakeBundle(EvidenceStatus.Contradictory);
        // Two Verified items disagree on the same file's change kind.
        e.ChangedFiles.Add(new ChangedFile("src/Foo.cs", FileChangeKind.Added, null, Prov(runId: "run-1")));
        e.ChangedFiles.Add(new ChangedFile("src/Foo.cs", FileChangeKind.Deleted, null, Prov(runId: "run-2")));

        var guidance = EvidenceRecoveryAdvisor.Advise(e);

        Assert.Equal(RecoveryAction.Reconcile, guidance.Action);
        Assert.Contains("src/Foo.cs", guidance.Reason);
    }

    [Fact]
    public void Contradictory_NoVerifiedFiles_ReasonMentionsUnknownPaths()
    {
        // Contradictory status set explicitly but no ChangedFiles with conflicting Verified items.
        var guidance = EvidenceRecoveryAdvisor.Advise(MakeBundle(EvidenceStatus.Contradictory));

        Assert.Equal(RecoveryAction.Reconcile, guidance.Action);
        Assert.Contains("unknown paths", guidance.Reason);
    }

    // ── Partial ───────────────────────────────────────────────────────────────

    [Fact]
    public void Partial_ReturnsRecapture()
    {
        var guidance = EvidenceRecoveryAdvisor.Advise(MakeBundle(EvidenceStatus.Partial));

        Assert.Equal(RecoveryAction.Recapture, guidance.Action);
        Assert.Contains("agent claims", guidance.Reason);
    }

    [Fact]
    public void Partial_AffectedRunIds_Listed()
    {
        var e = MakeBundle(EvidenceStatus.Partial, "run-partial");
        var guidance = EvidenceRecoveryAdvisor.Advise(e);

        Assert.Contains("run-partial", guidance.AffectedRunIds);
    }

    // ── Complete ──────────────────────────────────────────────────────────────

    [Fact]
    public void Complete_ReturnsNone()
    {
        var guidance = EvidenceRecoveryAdvisor.Advise(MakeBundle(EvidenceStatus.Complete));

        Assert.Equal(RecoveryAction.None, guidance.Action);
        Assert.Contains("complete and verified", guidance.Reason);
    }

    [Fact]
    public void Complete_AffectedRunIds_StillListed()
    {
        var e = MakeBundle(EvidenceStatus.Complete, "run-good");
        var guidance = EvidenceRecoveryAdvisor.Advise(e);

        Assert.Contains("run-good", guidance.AffectedRunIds);
    }

    // ── Multi-run: RunIds preserved across all states ─────────────────────────

    [Fact]
    public void MultiRun_AllRunIdsListedInGuidance()
    {
        var e = MakeBundle(EvidenceStatus.Missing);
        e.RunIds.Add("run-2");
        e.RunIds.Add("run-3");

        var guidance = EvidenceRecoveryAdvisor.Advise(e);

        Assert.Contains("run-1", guidance.AffectedRunIds);
        Assert.Contains("run-2", guidance.AffectedRunIds);
        Assert.Contains("run-3", guidance.AffectedRunIds);
    }

    // ── DecisionBrief integration: RecoveryGuidance is populated ─────────────

    [Fact]
    public void DecisionBrief_IncludesRecoveryGuidance_ForMissingEvidence()
    {
        var e = MakeBundle(EvidenceStatus.Missing);
        var brief = DecisionBriefComposer.Compose(e);

        Assert.NotNull(brief.RecoveryGuidance);
        Assert.Equal(RecoveryAction.Recapture, brief.RecoveryGuidance.Action);
    }

    [Fact]
    public void DecisionBrief_IncludesRecoveryGuidance_None_WhenComplete()
    {
        var e = MakeBundle(EvidenceStatus.Complete);
        e.CommandsRun.Add(new CommandRecord("dotnet test", null, 0, true, null, 10, 0, Prov()));
        e.ChangedFiles.Add(new ChangedFile("Foo.cs", FileChangeKind.Modified, null, Prov()));
        var brief = DecisionBriefComposer.Compose(e);

        Assert.Equal(RecoveryAction.None, brief.RecoveryGuidance.Action);
    }
}
