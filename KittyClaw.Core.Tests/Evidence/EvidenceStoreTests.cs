using KittyClaw.Core.Evidence;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Evidence;

public sealed class EvidenceStoreTests : IDisposable
{
    private readonly TempDir _tmp;
    private readonly EvidenceStore _store;

    public EvidenceStoreTests()
    {
        _tmp = new TempDir();
        _store = new EvidenceStore(_tmp.Path);
    }

    public void Dispose() => _tmp.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static EvidenceProvenance MakeProv(string source = "process-exit",
        string provider = "claude", string runId = "run-1") =>
        new(source, provider, runId, DateTime.UtcNow, EvidenceTrust.Verified);

    private static TicketEvidence MakeEvidence(string ticketId = "42",
        string runId = "run-1", string provider = "claude")
    {
        var prov = MakeProv(provider: provider, runId: runId);
        var e = new TicketEvidence
        {
            TicketId = ticketId,
            ProjectSlug = "test-proj",
            CapturedAt = DateTime.UtcNow,
        };
        e.RunIds.Add(runId);
        e.CommandsRun.Add(new CommandRecord(
            Command: provider, WorkingDirectory: "/workspace",
            ExitCode: 0, Success: true, OutputSummary: "status=Completed",
            TestsPassed: null, TestsFailed: null, Provenance: prov));
        e.RepositoryState = new RepositoryState(
            Branch: "main", CommitSha: "abc123", IsClean: true,
            UntrackedFiles: [], Provenance: prov);
        e.Status = ProvenanceRules.ComputeStatus(e);
        return e;
    }

    // ── SaveRun / LoadRun round-trip ──────────────────────────────────────────

    [Fact]
    public void SaveRun_ThenLoadRun_ReturnsEquivalentBundle()
    {
        var evidence = MakeEvidence(runId: "run-rt");

        _store.SaveRun(evidence);
        var loaded = _store.LoadRun("run-rt");

        Assert.NotNull(loaded);
        Assert.Equal(evidence.TicketId, loaded!.TicketId);
        Assert.Equal(evidence.ProjectSlug, loaded.ProjectSlug);
        Assert.Equal("run-rt", Assert.Single(loaded.RunIds));
        Assert.Equal(EvidenceStatus.Complete, loaded.Status);
        Assert.Single(loaded.CommandsRun);
        Assert.NotNull(loaded.RepositoryState);
    }

    [Fact]
    public void LoadRun_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.LoadRun("no-such-run"));
    }

    // ── LoadTicket ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadTicket_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.LoadTicket("99"));
    }

    // ── MergeAndSave ─────────────────────────────────────────────────────────

    [Fact]
    public void MergeAndSave_FirstRun_CreatesTicketBundle()
    {
        var run1 = MakeEvidence(ticketId: "10", runId: "run-a");

        var merged = _store.MergeAndSave(run1);

        Assert.Equal("10", merged.TicketId);
        Assert.Contains("run-a", merged.RunIds);
        Assert.Single(merged.CommandsRun);
        Assert.Equal(EvidenceStatus.Complete, merged.Status);

        var loaded = _store.LoadTicket("10");
        Assert.NotNull(loaded);
        Assert.Contains("run-a", loaded!.RunIds);
    }

    [Fact]
    public void MergeAndSave_SecondRun_AppendsRunIdAndItems()
    {
        var run1 = MakeEvidence(ticketId: "20", runId: "run-a", provider: "claude");
        var run2 = MakeEvidence(ticketId: "20", runId: "run-b", provider: "grok");

        _store.MergeAndSave(run1);
        var merged = _store.MergeAndSave(run2);

        Assert.Equal(2, merged.RunIds.Count);
        Assert.Contains("run-a", merged.RunIds);
        Assert.Contains("run-b", merged.RunIds);
        Assert.Equal(2, merged.CommandsRun.Count);
    }

    [Fact]
    public void MergeAndSave_DuplicateRunId_NotAddedTwice()
    {
        var run1 = MakeEvidence(ticketId: "30", runId: "run-dup");
        _store.MergeAndSave(run1);

        // Merge same run-id again (e.g. crash-restart scenario).
        var run1Again = MakeEvidence(ticketId: "30", runId: "run-dup");
        var merged = _store.MergeAndSave(run1Again);

        Assert.Single(merged.RunIds);
    }

    [Fact]
    public void MergeAndSave_SourceIdentityPreserved_EachItemKeepsOriginalRunId()
    {
        var run1 = MakeEvidence(ticketId: "40", runId: "run-first", provider: "claude");
        var run2 = MakeEvidence(ticketId: "40", runId: "run-second", provider: "grok");

        _store.MergeAndSave(run1);
        var merged = _store.MergeAndSave(run2);

        var firstCmd = merged.CommandsRun.First(c => c.Provenance.Provider == "claude");
        var secondCmd = merged.CommandsRun.First(c => c.Provenance.Provider == "grok");

        Assert.Equal("run-first", firstCmd.Provenance.RunId);
        Assert.Equal("run-second", secondCmd.Provenance.RunId);
    }

    [Fact]
    public void MergeAndSave_LatestRunRepositoryState_Wins()
    {
        var run1 = MakeEvidence(ticketId: "50", runId: "run-old");
        run1.RepositoryState = new RepositoryState(
            Branch: "old-branch", CommitSha: "old-sha", IsClean: true,
            UntrackedFiles: [], Provenance: MakeProv(runId: "run-old"));

        var run2 = MakeEvidence(ticketId: "50", runId: "run-new");
        run2.RepositoryState = new RepositoryState(
            Branch: "new-branch", CommitSha: "new-sha", IsClean: false,
            UntrackedFiles: ["file.cs"], Provenance: MakeProv(runId: "run-new"));

        _store.MergeAndSave(run1);
        var merged = _store.MergeAndSave(run2);

        Assert.Equal("new-branch", merged.RepositoryState!.Branch);
        Assert.Equal("new-sha", merged.RepositoryState.CommitSha);
    }

    [Fact]
    public void MergeAndSave_RunEvidenceSavedSeparately()
    {
        var run1 = MakeEvidence(ticketId: "60", runId: "run-separate");
        _store.MergeAndSave(run1);

        var runLoaded = _store.LoadRun("run-separate");
        Assert.NotNull(runLoaded);
        Assert.Equal("60", runLoaded!.TicketId);
    }

    // ── ApplyStaleness ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyStaleness_FreshBundle_StatusUnchanged()
    {
        var evidence = MakeEvidence();
        evidence.Status = EvidenceStatus.Complete;

        EvidenceStore.ApplyStaleness(evidence, TimeSpan.FromHours(24), DateTime.UtcNow);

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
    }

    [Fact]
    public void ApplyStaleness_OldBundle_SetsStale()
    {
        var evidence = new TicketEvidence
        {
            TicketId = "1",
            ProjectSlug = "p",
            CapturedAt = DateTime.UtcNow - TimeSpan.FromHours(25),
            Status = EvidenceStatus.Complete,
        };

        EvidenceStore.ApplyStaleness(evidence, TimeSpan.FromHours(24), DateTime.UtcNow);

        Assert.Equal(EvidenceStatus.Stale, evidence.Status);
    }

    [Fact]
    public void ApplyStaleness_AlreadyStale_RemainsStale()
    {
        var evidence = new TicketEvidence
        {
            TicketId = "1",
            ProjectSlug = "p",
            CapturedAt = DateTime.UtcNow,
            Status = EvidenceStatus.Stale,
        };

        // asOf is in the past — would not trigger staleness on its own.
        EvidenceStore.ApplyStaleness(evidence, TimeSpan.FromHours(24),
            DateTime.UtcNow - TimeSpan.FromHours(48));

        Assert.Equal(EvidenceStatus.Stale, evidence.Status);
    }

    [Fact]
    public void ApplyStaleness_MissingStatus_SetsStaleWhenOld()
    {
        var evidence = new TicketEvidence
        {
            TicketId = "1",
            ProjectSlug = "p",
            CapturedAt = DateTime.UtcNow - TimeSpan.FromDays(2),
            Status = EvidenceStatus.Missing,
        };

        EvidenceStore.ApplyStaleness(evidence, TimeSpan.FromHours(24), DateTime.UtcNow);

        Assert.Equal(EvidenceStatus.Stale, evidence.Status);
    }

    // ── Corrupt file handling ─────────────────────────────────────────────────

    [Fact]
    public void LoadRun_CorruptFile_ReturnsNull()
    {
        var path = Path.Combine(_tmp.Path, "runs", "bad-run.evidence.json");
        File.WriteAllText(path, "not valid json {{{");

        Assert.Null(_store.LoadRun("bad-run"));
    }
}
