using KittyClaw.Core.Evidence;

namespace KittyClaw.Core.Tests.Evidence;

public sealed class TicketDecisionBriefTests
{
    private static EvidenceProvenance Prov(EvidenceTrust trust = EvidenceTrust.Verified, string runId = "run-1") =>
        new("process-exit", "claude", runId, DateTime.UtcNow, trust);

    private static TicketEvidence MakeEvidence(
        EvidenceStatus status = EvidenceStatus.Complete,
        bool? repoClean = true,
        bool commandSucceeded = true,
        int testsPassed = 5,
        int testsFailed = 0,
        EvidenceTrust commandTrust = EvidenceTrust.Verified)
    {
        var e = new TicketEvidence
        {
            TicketId = "42",
            ProjectSlug = "test",
            CapturedAt = DateTime.UtcNow,
            Status = status,
        };
        e.RunIds.Add("run-1");
        e.CommandsRun.Add(new CommandRecord(
            Command: "dotnet test",
            WorkingDirectory: "/ws",
            ExitCode: commandSucceeded ? 0 : 1,
            Success: commandSucceeded,
            OutputSummary: null,
            TestsPassed: testsPassed,
            TestsFailed: testsFailed,
            Provenance: Prov(commandTrust)));
        e.ChangedFiles.Add(new ChangedFile("src/Foo.cs", FileChangeKind.Modified, null, Prov()));
        if (repoClean.HasValue)
            e.RepositoryState = new RepositoryState("ticket/42", "abc", repoClean, [], Prov())
                { BaseCommitSha = "base" };
        return e;
    }

    [Fact]
    public void CompleteEvidence_AllCommandsPass_ProducesShip()
    {
        var brief = DecisionBriefComposer.Compose(MakeEvidence(EvidenceStatus.Complete));

        Assert.Equal(DecisionVerdict.Ship, brief.Verdict);
        Assert.DoesNotContain(brief.Findings, f => f.IsBlocking);
    }

    [Fact]
    public void PartialEvidence_AllCommandsPass_ProducesFix()
    {
        var brief = DecisionBriefComposer.Compose(
            MakeEvidence(EvidenceStatus.Partial, commandTrust: EvidenceTrust.AgentClaim));

        Assert.Equal(DecisionVerdict.Fix, brief.Verdict);
        Assert.Contains(brief.Findings, f => f.Category == "agent-claim" && !f.IsBlocking);
    }

    [Fact]
    public void FailedCommand_ProducesFix()
    {
        var brief = DecisionBriefComposer.Compose(
            MakeEvidence(EvidenceStatus.Partial, commandSucceeded: false, testsFailed: 3,
                commandTrust: EvidenceTrust.AgentClaim));

        Assert.Equal(DecisionVerdict.Fix, brief.Verdict);
        Assert.Contains(brief.Findings, f => f.Category == "command-failure" && f.IsBlocking);
    }

    [Fact]
    public void CompleteEvidence_FailedCommand_ProducesFix_NotBlock()
    {
        var brief = DecisionBriefComposer.Compose(
            MakeEvidence(EvidenceStatus.Complete, commandSucceeded: false, testsFailed: 2));

        Assert.Equal(DecisionVerdict.Fix, brief.Verdict);
    }

    [Fact]
    public void MissingEvidence_ProducesBlock()
    {
        var brief = DecisionBriefComposer.Compose(MakeEvidence(EvidenceStatus.Missing));

        Assert.Equal(DecisionVerdict.Block, brief.Verdict);
        Assert.Contains(brief.Findings, f => f.Category == "missing-evidence" && f.IsBlocking);
    }

    [Fact]
    public void StaleEvidence_ProducesBlock()
    {
        var brief = DecisionBriefComposer.Compose(MakeEvidence(EvidenceStatus.Stale));

        Assert.Equal(DecisionVerdict.Block, brief.Verdict);
        Assert.Contains(brief.Findings, f => f.Category == "stale-evidence" && f.IsBlocking);
    }

    [Fact]
    public void ContradictoryEvidence_ProducesBlock()
    {
        var brief = DecisionBriefComposer.Compose(MakeEvidence(EvidenceStatus.Contradictory));

        Assert.Equal(DecisionVerdict.Block, brief.Verdict);
        Assert.Contains(brief.Findings, f => f.Category == "contradictory-evidence" && f.IsBlocking);
    }

    [Fact]
    public void TestCountsAreSummedAcrossAllCommands()
    {
        var e = new TicketEvidence { TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow, Status = EvidenceStatus.Complete };
        e.RunIds.Add("r1");
        e.CommandsRun.Add(new CommandRecord("cmd1", null, 0, true, null, 10, 0, Prov()));
        e.CommandsRun.Add(new CommandRecord("cmd2", null, 0, true, null, 5, 1, Prov()));

        var brief = DecisionBriefComposer.Compose(e);

        Assert.Equal(15, brief.TestsPassed);
        Assert.Equal(1, brief.TestsFailed);
    }

    [Fact]
    public void FileCount_MatchesChangedFilesCount()
    {
        var e = new TicketEvidence { TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow, Status = EvidenceStatus.Complete };
        e.ChangedFiles.Add(new ChangedFile("a.cs", FileChangeKind.Added, null, Prov()));
        e.ChangedFiles.Add(new ChangedFile("b.cs", FileChangeKind.Modified, null, Prov()));

        var brief = DecisionBriefComposer.Compose(e);

        Assert.Equal(2, brief.FilesChanged);
        Assert.Equal(new[] { "a.cs", "b.cs" }, brief.ChangedFiles.Select(f => f.Path));
    }

    [Fact]
    public void RunIds_AreCopiedFromEvidence()
    {
        var e = new TicketEvidence { TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow, Status = EvidenceStatus.Complete };
        e.RunIds.Add("run-a");
        e.RunIds.Add("run-b");

        var brief = DecisionBriefComposer.Compose(e);

        Assert.Equal(new[] { "run-a", "run-b" }, brief.RunIds);
    }

    [Fact]
    public void RepositoryClean_CopiedFromRepositoryState()
    {
        var brief = DecisionBriefComposer.Compose(MakeEvidence(repoClean: false));

        Assert.Equal(false, brief.RepositoryClean);
        Assert.Equal("ticket/42", brief.RepositoryBranch);
        Assert.Equal("abc", brief.RepositoryCommitSha);
        Assert.Equal("base", brief.RepositoryBaseCommitSha);
    }

    [Fact]
    public void NoRepositoryState_RepositoryCleanIsNull()
    {
        var brief = DecisionBriefComposer.Compose(MakeEvidence(repoClean: null));

        Assert.Null(brief.RepositoryClean);
    }
}
