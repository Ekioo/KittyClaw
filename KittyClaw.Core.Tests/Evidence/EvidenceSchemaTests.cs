using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Evidence;

namespace KittyClaw.Core.Tests.Evidence;

public class EvidenceSchemaTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static EvidenceProvenance Verified(string source = "git", string runId = "run-1") =>
        new(source, "claude", runId, DateTime.UtcNow, EvidenceTrust.Verified);

    private static EvidenceProvenance Claim(string runId = "run-1") =>
        new("agent-claim", "claude", runId, DateTime.UtcNow, EvidenceTrust.AgentClaim);

    private static EvidenceProvenance MissingProv(string runId = "run-1") =>
        new("git", "claude", runId, DateTime.UtcNow, EvidenceTrust.Missing);

    // --- Round-trip serialization ---

    [Fact]
    public void TicketEvidence_RoundTrips_ThroughJson()
    {
        var ev = new TicketEvidence
        {
            TicketId = "42",
            ProjectSlug = "proj",
            CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = EvidenceStatus.Complete,
            RunIds = ["run-1"],
            ChangedFiles =
            [
                new ChangedFile("src/Foo.cs", FileChangeKind.Modified, null, Verified()),
            ],
            CommandsRun =
            [
                new CommandRecord("dotnet test", null, 0, true, "1 passed", 1, 0,
                    Verified("process-exit")),
            ],
            RepositoryState = new RepositoryState("main", "abc123", true, [],
                Verified("git")),
            Retries =
            [
                new RetryRecord(1, "quota exceeded", Verified()),
            ],
        };

        var json = JsonSerializer.Serialize(ev, s_json);
        var back = JsonSerializer.Deserialize<TicketEvidence>(json, s_json);

        Assert.NotNull(back);
        Assert.Equal("42", back!.TicketId);
        Assert.Equal("proj", back.ProjectSlug);
        Assert.Equal(EvidenceStatus.Complete, back.Status);
        Assert.Single(back.RunIds);

        Assert.Single(back.ChangedFiles);
        Assert.Equal("src/Foo.cs", back.ChangedFiles[0].Path);
        Assert.Equal(FileChangeKind.Modified, back.ChangedFiles[0].Kind);
        Assert.Equal(EvidenceTrust.Verified, back.ChangedFiles[0].Provenance.Trust);
        Assert.Equal("git", back.ChangedFiles[0].Provenance.Source);
        Assert.Equal("claude", back.ChangedFiles[0].Provenance.Provider);

        Assert.Single(back.CommandsRun);
        Assert.True(back.CommandsRun[0].Success);
        Assert.Equal(1, back.CommandsRun[0].TestsPassed);

        Assert.NotNull(back.RepositoryState);
        Assert.Equal("main", back.RepositoryState!.Branch);
        Assert.True(back.RepositoryState.IsClean);

        Assert.Single(back.Retries);
        Assert.Equal(1, back.Retries[0].AttemptNumber);
    }

    [Fact]
    public void EvidenceStatus_SerializesAsString_NotInteger()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p",
            CapturedAt = DateTime.UtcNow, Status = EvidenceStatus.Partial,
        };
        var json = JsonSerializer.Serialize(ev, s_json);
        Assert.Contains("\"partial\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"status\":1", json);
    }

    [Fact]
    public void EvidenceTrust_SerializesAsString_NotInteger()
    {
        var prov = Verified();
        var json = JsonSerializer.Serialize(prov, s_json);
        // The trust field must appear as a JSON string value, not as a numeric 0
        Assert.Contains("\"trust\":\"Verified\"", json, StringComparison.OrdinalIgnoreCase);
    }

    // --- ComputeStatus: empty bundle ---

    [Fact]
    public void ComputeStatus_EmptyBundle_ReturnsMissing()
    {
        var ev = new TicketEvidence
        { TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow };
        Assert.Equal(EvidenceStatus.Missing, ProvenanceRules.ComputeStatus(ev));
    }

    // --- ComputeStatus: complete ---

    [Fact]
    public void ComputeStatus_AllVerified_ReturnsComplete()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles = [new ChangedFile("a.cs", FileChangeKind.Added, null, Verified())],
            CommandsRun = [new CommandRecord("dotnet test", null, 0, true, null, null, null,
                Verified("process-exit"))],
        };
        Assert.Equal(EvidenceStatus.Complete, ProvenanceRules.ComputeStatus(ev));
    }

    [Fact]
    public void ComputeStatus_OnlyRepoState_AllVerified_ReturnsComplete()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            RepositoryState = new RepositoryState("main", "abc", true, [], Verified("git")),
        };
        Assert.Equal(EvidenceStatus.Complete, ProvenanceRules.ComputeStatus(ev));
    }

    // --- ComputeStatus: partial ---

    [Fact]
    public void ComputeStatus_OneAgentClaim_ReturnsPartial()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles = [new ChangedFile("a.cs", FileChangeKind.Added, null, Claim())],
        };
        Assert.Equal(EvidenceStatus.Partial, ProvenanceRules.ComputeStatus(ev));
    }

    [Fact]
    public void ComputeStatus_MixedVerifiedAndClaim_ReturnsPartial()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles =
            [
                new ChangedFile("a.cs", FileChangeKind.Added, null, Verified()),
                new ChangedFile("b.cs", FileChangeKind.Modified, null, Claim()),
            ],
        };
        Assert.Equal(EvidenceStatus.Partial, ProvenanceRules.ComputeStatus(ev));
    }

    // --- ComputeStatus: missing ---

    [Fact]
    public void ComputeStatus_MissingTrustItem_ReturnsMissing()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles = [new ChangedFile("a.cs", FileChangeKind.Added, null, MissingProv())],
        };
        Assert.Equal(EvidenceStatus.Missing, ProvenanceRules.ComputeStatus(ev));
    }

    [Fact]
    public void ComputeStatus_MissingRetryProvenance_ReturnsMissing()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles = [new ChangedFile("a.cs", FileChangeKind.Added, null, Verified())],
            Retries = [new RetryRecord(1, "quota", MissingProv())],
        };
        Assert.Equal(EvidenceStatus.Missing, ProvenanceRules.ComputeStatus(ev));
    }

    // --- ComputeStatus: contradictory ---

    [Fact]
    public void ComputeStatus_TwoVerifiedFilesDisagreeOnKind_ReturnsContradictory()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles =
            [
                new ChangedFile("a.cs", FileChangeKind.Added, null, Verified(runId: "run-1")),
                new ChangedFile("a.cs", FileChangeKind.Modified, null, Verified(runId: "run-2")),
            ],
        };
        Assert.Equal(EvidenceStatus.Contradictory, ProvenanceRules.ComputeStatus(ev));
    }

    [Fact]
    public void ComputeStatus_SamePathDifferentKind_ButNotVerified_DoesNotReturnContradictory()
    {
        // Two AgentClaim items for same path with different kinds → Partial, not Contradictory
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles =
            [
                new ChangedFile("a.cs", FileChangeKind.Added, null, Claim()),
                new ChangedFile("a.cs", FileChangeKind.Modified, null, Claim()),
            ],
        };
        Assert.Equal(EvidenceStatus.Partial, ProvenanceRules.ComputeStatus(ev));
    }

    [Fact]
    public void ComputeStatus_TwoVerifiedFilesAgreeSameKind_DoesNotReturnContradictory()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles =
            [
                new ChangedFile("a.cs", FileChangeKind.Added, null, Verified(runId: "run-1")),
                new ChangedFile("a.cs", FileChangeKind.Added, null, Verified(runId: "run-2")),
            ],
        };
        Assert.Equal(EvidenceStatus.Complete, ProvenanceRules.ComputeStatus(ev));
    }

    [Fact]
    public void ComputeStatus_PathComparisonIsCaseInsensitive()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles =
            [
                new ChangedFile("SRC/Foo.cs", FileChangeKind.Added, null, Verified(runId: "run-1")),
                new ChangedFile("src/foo.cs", FileChangeKind.Modified, null, Verified(runId: "run-2")),
            ],
        };
        Assert.Equal(EvidenceStatus.Contradictory, ProvenanceRules.ComputeStatus(ev));
    }

    // --- ProvenanceRules helpers ---

    [Theory]
    [InlineData("git", true)]
    [InlineData("process-exit", true)]
    [InlineData("filesystem", true)]
    [InlineData("test-runner", true)]
    [InlineData("GIT", true)]
    [InlineData("agent-claim", false)]
    [InlineData("free-text", false)]
    [InlineData("", false)]
    public void IsVerifiedSource_ReturnsExpected(string source, bool expected)
    {
        Assert.Equal(expected, ProvenanceRules.IsVerifiedSource(source));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("grok", true)]
    [InlineData("mistral", true)]
    [InlineData("unknown", true)]
    [InlineData("CLAUDE", true)]
    [InlineData("openai", false)]
    [InlineData("ollama", false)]
    [InlineData("", false)]
    public void IsKnownProvider_ReturnsExpected(string provider, bool expected)
    {
        Assert.Equal(expected, ProvenanceRules.IsKnownProvider(provider));
    }

    // --- Priority order: Contradictory wins over Missing ---

    [Fact]
    public void ComputeStatus_ContradictoryAndMissing_ReturnsContradictory()
    {
        var ev = new TicketEvidence
        {
            TicketId = "1", ProjectSlug = "p", CapturedAt = DateTime.UtcNow,
            ChangedFiles =
            [
                new ChangedFile("a.cs", FileChangeKind.Added, null, Verified(runId: "run-1")),
                new ChangedFile("a.cs", FileChangeKind.Modified, null, Verified(runId: "run-2")),
                new ChangedFile("b.cs", FileChangeKind.Added, null, MissingProv()),
            ],
        };
        Assert.Equal(EvidenceStatus.Contradictory, ProvenanceRules.ComputeStatus(ev));
    }
}
