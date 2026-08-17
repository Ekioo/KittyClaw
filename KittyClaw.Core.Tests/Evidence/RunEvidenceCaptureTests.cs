using KittyClaw.Core.Automation;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Evidence;

public sealed class RunEvidenceCaptureTests : IAsyncLifetime
{
    private TempDir _tmp = null!;
    private string _gitRepo = "";

    public async Task InitializeAsync()
    {
        _tmp = new TempDir();
        _gitRepo = Path.Combine(_tmp.Path, "repo");
        Directory.CreateDirectory(_gitRepo);
        await Git("init");
        await Git("config user.email test@example.com");
        await Git("config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(_gitRepo, "readme.txt"), "hello");
        await Git("add .");
        await Git("commit -m init");
        await Git("update-ref refs/remotes/origin/dev HEAD");
    }

    public Task DisposeAsync()
    {
        _tmp.Dispose();
        return Task.CompletedTask;
    }

    private Task Git(string args) =>
        ProcessRunner.RunAsync("git", args, workingDirectory: _gitRepo,
            timeout: TimeSpan.FromSeconds(15));

    private static AgentRun MakeRun(string runId = "run-1", int? exitCode = 0,
        AgentRunStatus status = AgentRunStatus.Completed)
    {
        var run = new AgentRun
        {
            RunId = runId,
            ProjectSlug = "test-proj",
            TicketId = 42,
            AgentName = "programmer",
            SkillFile = "programmer/SKILL.md",
            ConcurrencyGroup = "programmer",
            StartedAt = DateTime.UtcNow,
        };
        run.Status = status;
        run.ExitCode = exitCode;
        return run;
    }

    // ── ProviderName mapping ───────────────────────────────────────────────

    [Theory]
    [InlineData(CliProvider.Claude, "claude")]
    [InlineData(CliProvider.Codex, "codex")]
    [InlineData(CliProvider.Grok, "grok")]
    [InlineData(CliProvider.Mistral, "mistral")]
    public void ProviderName_MapsAllKnownProviders(CliProvider provider, string expected) =>
        Assert.Equal(expected, RunEvidenceCapture.ProviderName(provider));

    // ── Process exit → CommandRecord ───────────────────────────────────────

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("grok")]
    public async Task CaptureAsync_AllProviders_ProcessExitIsVerified(string provider)
    {
        var run = MakeRun(exitCode: 0);

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, provider);

        var cmd = Assert.Single(evidence.CommandsRun);
        Assert.Equal(provider, cmd.Command);
        Assert.Equal(0, cmd.ExitCode);
        Assert.True(cmd.Success);
        Assert.Equal("process-exit", cmd.Provenance.Source);
        Assert.Equal(provider, cmd.Provenance.Provider);
        Assert.Equal(EvidenceTrust.Verified, cmd.Provenance.Trust);
        Assert.Equal(run.RunId, cmd.Provenance.RunId);
    }

    [Fact]
    public async Task RunEvidenceAttacher_AttachAsync_PersistsProjectScopedRunAndTicketBundles()
    {
        var projects = new ProjectService(Path.Combine(_tmp.Path, "data"));
        var project = await projects.CreateProjectAsync("Test Project");
        await projects.UpdateProjectAsync(project.Slug, _gitRepo);
        var store = new EvidenceStore(Path.Combine(_tmp.Path, "evidence-store"));
        var attacher = new RunEvidenceAttacher(
            new AgentRunRegistry(), store, projects, NullLogger<RunEvidenceAttacher>.Instance);
        var run = MakeRun("attach-run");
        run = new AgentRun
        {
            RunId = run.RunId,
            ProjectSlug = project.Slug,
            TicketId = run.TicketId,
            AgentName = run.AgentName,
            SkillFile = run.SkillFile,
            ConcurrencyGroup = run.ConcurrencyGroup,
            StartedAt = run.StartedAt,
            Model = "claude-sonnet-4-6",
            Status = AgentRunStatus.Completed,
            ExitCode = 0,
        };

        await attacher.AttachAsync(run);

        var runEvidence = store.LoadRun(project.Slug, run.RunId);
        var ticketEvidence = store.LoadTicket(project.Slug, run.TicketId!.Value.ToString());
        Assert.NotNull(runEvidence);
        Assert.NotNull(ticketEvidence);
        Assert.Equal(project.Slug, runEvidence!.ProjectSlug);
        Assert.All(runEvidence.CommandsRun, item => Assert.Equal(run.RunId, item.Provenance.RunId));
        Assert.Equal(run.RunId, Assert.Single(ticketEvidence!.RunIds));
    }

    [Fact]
    public async Task CaptureAsync_FailedRun_CommandRecordSuccessFalse()
    {
        var run = MakeRun(exitCode: 1, status: AgentRunStatus.Failed);

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        var cmd = Assert.Single(evidence.CommandsRun);
        Assert.Equal(1, cmd.ExitCode);
        Assert.False(cmd.Success);
        Assert.Equal(EvidenceTrust.Verified, cmd.Provenance.Trust);
    }

    [Fact]
    public async Task CaptureAsync_StoppedRunNullExitCode_CommandRecordSuccessFalse()
    {
        var run = MakeRun(exitCode: null, status: AgentRunStatus.Stopped);

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        var cmd = Assert.Single(evidence.CommandsRun);
        Assert.Null(cmd.ExitCode);
        Assert.False(cmd.Success);
        Assert.Equal(EvidenceTrust.Verified, cmd.Provenance.Trust);
    }

    // ── Bundle metadata ────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_BundleMetadata_IsCorrect()
    {
        var run = MakeRun("run-abc");

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        Assert.Equal("42", evidence.TicketId);
        Assert.Equal("test-proj", evidence.ProjectSlug);
        Assert.Equal("run-abc", Assert.Single(evidence.RunIds));
        Assert.True(evidence.CapturedAt <= DateTime.UtcNow);
    }

    // ── Retry records ─────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_ResetAndFallbackEvents_ProduceRetryRecords()
    {
        var run = MakeRun();
        run.Push(new StreamEvent(DateTime.UtcNow, "reset", "Previous session expired, starting fresh"));
        run.Push(new StreamEvent(DateTime.UtcNow, "fallback", "Quota reached — retrying with fallback"));

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "grok");

        Assert.Equal(2, evidence.Retries.Count);
        Assert.Equal(1, evidence.Retries[0].AttemptNumber);
        Assert.Contains("expired", evidence.Retries[0].FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, evidence.Retries[1].AttemptNumber);
        Assert.Contains("Quota", evidence.Retries[1].FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.All(evidence.Retries, r => Assert.Equal(EvidenceTrust.Verified, r.Provenance.Trust));
        Assert.All(evidence.Retries, r => Assert.Equal("process-exit", r.Provenance.Source));
    }

    [Fact]
    public async Task CaptureAsync_NoRetryEvents_EmptyRetryList()
    {
        var run = MakeRun();
        // Only assistant/tool events — must not appear as retries.
        run.Push(new StreamEvent(DateTime.UtcNow, "assistant", "I fixed the bug."));
        run.Push(new StreamEvent(DateTime.UtcNow, "tool_use", "bash", "{\"command\":\"dotnet test\"}"));

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        Assert.Empty(evidence.Retries);
    }

    // ── Git probing ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("grok")]
    public async Task CaptureAsync_AllProviders_GitRepoState_IsVerified(string provider)
    {
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, provider);

        Assert.NotNull(evidence.RepositoryState);
        Assert.Equal("git", evidence.RepositoryState!.Provenance.Source);
        Assert.Equal(provider, evidence.RepositoryState.Provenance.Provider);
        Assert.Equal(EvidenceTrust.Verified, evidence.RepositoryState.Provenance.Trust);
        Assert.False(string.IsNullOrWhiteSpace(evidence.RepositoryState.CommitSha));
    }

    [Fact]
    public async Task CaptureAsync_CleanRepo_IsCleanTrue()
    {
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        Assert.NotNull(evidence.RepositoryState!.IsClean);
        Assert.True(evidence.RepositoryState.IsClean);
        Assert.Empty(evidence.ChangedFiles);
    }

    [Fact]
    public async Task CaptureAsync_DirtyWorkspace_DoesNotCountTransientModificationAsDelivered()
    {
        await File.WriteAllTextAsync(Path.Combine(_gitRepo, "readme.txt"), "modified");
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        Assert.NotNull(evidence.RepositoryState);
        Assert.False(evidence.RepositoryState!.IsClean);
        Assert.Empty(evidence.ChangedFiles);
    }

    [Fact]
    public async Task CaptureAsync_StagedTransientFile_DoesNotAppearAsDelivered()
    {
        var newFile = Path.Combine(_gitRepo, "new.cs");
        await File.WriteAllTextAsync(newFile, "class New {}");
        await Git("add new.cs");
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "codex");

        Assert.DoesNotContain(evidence.ChangedFiles, f => f.Path.Contains("new.cs"));
    }

    [Fact]
    public async Task CaptureAsync_CleanTicketBranch_ReportsExactlyCommittedFilesFromStableBase()
    {
        await File.WriteAllTextAsync(Path.Combine(_gitRepo, "report.md"), "report");
        await File.WriteAllTextAsync(Path.Combine(_gitRepo, "memory.md"), "memory");
        await Git("add report.md memory.md");
        await Git("commit -m ticket");

        var evidence = await RunEvidenceCapture.CaptureAsync(MakeRun(), _gitRepo, "codex");

        Assert.True(evidence.RepositoryState!.IsClean);
        Assert.False(string.IsNullOrWhiteSpace(evidence.RepositoryState.BaseCommitSha));
        Assert.Equal(new[] { "memory.md", "report.md" },
            evidence.ChangedFiles.Select(f => f.Path).OrderBy(p => p).ToArray());
        Assert.All(evidence.ChangedFiles, f => Assert.Equal(FileChangeKind.Added, f.Kind));
    }

    [Fact]
    public async Task CaptureAsync_UntrackedFile_AppearsInUntrackedList()
    {
        await File.WriteAllTextAsync(Path.Combine(_gitRepo, "untracked.txt"), "not staged");
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        Assert.NotNull(evidence.RepositoryState);
        Assert.False(evidence.RepositoryState!.IsClean);
        Assert.Contains("untracked.txt", evidence.RepositoryState.UntrackedFiles,
            StringComparer.OrdinalIgnoreCase);
    }

    // ── Partial / missing evidence ─────────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_NotGitRepo_RepositoryStateIsMissing()
    {
        var notRepo = Path.Combine(_tmp.Path, "notgit");
        Directory.CreateDirectory(notRepo);
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, notRepo, "claude");

        Assert.NotNull(evidence.RepositoryState);
        Assert.Equal(EvidenceTrust.Missing, evidence.RepositoryState!.Provenance.Trust);
        Assert.Null(evidence.RepositoryState.Branch);
        Assert.Null(evidence.RepositoryState.CommitSha);
        Assert.Null(evidence.RepositoryState.IsClean);
        Assert.Empty(evidence.ChangedFiles);
    }

    [Fact]
    public async Task CaptureAsync_NotGitRepo_StatusIsMissing()
    {
        var notRepo = Path.Combine(_tmp.Path, "notgit2");
        Directory.CreateDirectory(notRepo);
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, notRepo, "codex");

        // RepositoryState is Missing → ComputeStatus returns Missing
        Assert.Equal(EvidenceStatus.Missing, evidence.Status);
    }

    [Fact]
    public async Task CaptureAsync_GoodGitRepo_StatusIsComplete()
    {
        var run = MakeRun();

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "grok");

        // All items have Verified trust → Complete
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
    }

    // ── Trust invariants ───────────────────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_AssistantEventsAreIgnored_NeverAgentClaim()
    {
        var run = MakeRun();
        // Simulate a run with lots of model output — must never appear as evidence
        run.Push(new StreamEvent(DateTime.UtcNow, "assistant", "I've identified the issue: file.cs needs updating."));
        run.Push(new StreamEvent(DateTime.UtcNow, "assistant", "The changed files are: foo.cs, bar.cs."));
        run.Push(new StreamEvent(DateTime.UtcNow, "result", "[result]"));

        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "claude");

        // No AgentClaim items: model text is never used as evidence source
        var allProvenances = evidence.CommandsRun.Select(c => c.Provenance)
            .Concat(evidence.ChangedFiles.Select(f => f.Provenance))
            .Concat(evidence.Retries.Select(r => r.Provenance))
            .Concat(evidence.RepositoryState is not null
                ? new[] { evidence.RepositoryState.Provenance }
                : Array.Empty<EvidenceProvenance>());
        Assert.DoesNotContain(allProvenances, p => p.Trust == EvidenceTrust.AgentClaim);
    }

    [Fact]
    public async Task CaptureAsync_ProcessExitSource_IsNeverAgentClaim()
    {
        var run = MakeRun();
        var evidence = await RunEvidenceCapture.CaptureAsync(run, _gitRepo, "grok");

        Assert.All(evidence.CommandsRun,
            cmd => Assert.NotEqual(EvidenceTrust.AgentClaim, cmd.Provenance.Trust));
    }
}
