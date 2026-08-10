using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Evidence;

/// <summary>
/// Repeatable end-to-end benchmark suite for the evidence capture and decision brief pipeline.
///
/// Covers three runner paths (Claude, Codex, Grok), a crash/restart recovery path, a
/// partial-evidence path, and adversarial edge cases (no evidence, contradictory evidence).
/// All scenarios run against an isolated in-process web host with a throwaway data directory.
///
/// How to run:
///   dotnet test KittyClaw.Core.Tests --filter FullyQualifiedName~EvidenceBenchmarkSuiteTests
/// </summary>
[Collection("MockClaude")]
public sealed class EvidenceBenchmarkSuiteTests : IClassFixture<EvidenceBenchmarkSuiteTests.BenchmarkFactory>, IDisposable
{
    private readonly BenchmarkFactory _factory;
    private readonly HttpClient _client;

    public EvidenceBenchmarkSuiteTests(BenchmarkFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    // -------------------------------------------------------------------------
    // Scenario 1: Claude runner — complete evidence → Ship verdict
    // Exercises the brief endpoint end-to-end and verifies all response fields.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Claude_CompleteEvidence_BriefVerdictIsShip()
    {
        var (slug, ticketId) = await CreateProjectAndTicketAsync(
            "bench-s1-claude", "Implement rate limiting middleware for API endpoints");
        var run = await RunProviderAsync(slug, ticketId, CliProvider.Claude,
            "claude-sonnet-4-6", "default", initializeGit: true);
        var evidence = await WaitForEvidenceAsync(slug, ticketId, run.RunId);

        // Evidence endpoint: status = Complete, provider = claude
        var ev = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/evidence");
        Assert.Equal("Complete", ev!["status"]!.GetValue<string>());
        Assert.Equal("claude", ev["commandsRun"]![0]!["provenance"]!["provider"]!.GetValue<string>());

        // Brief endpoint: Ship verdict, all fields present
        var brief = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        Assert.Equal("Ship", brief!["verdict"]!.GetValue<string>());
        Assert.Equal("Complete", brief["evidenceStatus"]!.GetValue<string>());
        Assert.Equal(0, brief["filesChanged"]!.GetValue<int>());
        Assert.Equal(1, brief["commandsRun"]!.GetValue<int>());
        Assert.Equal(0, brief["testsPassed"]!.GetValue<int>());
        Assert.Equal(0, brief["testsFailed"]!.GetValue<int>());
        Assert.False(brief["repositoryClean"]!.GetValue<bool>());
        Assert.Contains(brief["runIds"]!.AsArray(), n => n!.GetValue<string>() == run.RunId);
        Assert.DoesNotContain(brief["findings"]!.AsArray(), f => f!["isBlocking"]!.GetValue<bool>());
        Assert.Equal("None", brief["recoveryGuidance"]!["action"]!.GetValue<string>());
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
    }

    // -------------------------------------------------------------------------
    // Scenario 2: Codex runner — incomplete-evidence path
    // The process result is captured, but the intentionally non-git workspace makes repository
    // evidence unavailable. ProvenanceRules correctly classifies any Missing observable as Missing.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Codex_IncompleteEvidence_AutomaticCaptureRequestsRecapture()
    {
        var (slug, ticketId) = await CreateProjectAndTicketAsync(
            "bench-s2-codex", "Refactor database connection pool to use async/await pattern");

        var run = await RunProviderAsync(slug, ticketId, CliProvider.Codex,
            "codex:gpt-5.6-sol", "default", initializeGit: false);
        var evidence = await WaitForEvidenceAsync(slug, ticketId, run.RunId);

        var ev = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/evidence");
        Assert.Equal("Missing", ev!["status"]!.GetValue<string>());
        Assert.Equal("codex", ev["commandsRun"]![0]!["provenance"]!["provider"]!.GetValue<string>());
        Assert.Equal("Missing", ev["repositoryState"]!["provenance"]!["trust"]!.GetValue<string>());

        var brief = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        Assert.Equal("Block", brief!["verdict"]!.GetValue<string>());
        Assert.Equal("Missing", brief["evidenceStatus"]!.GetValue<string>());

        // Automatic capture could observe process completion but not repository state.
        Assert.Equal("Recapture", brief["recoveryGuidance"]!["action"]!.GetValue<string>());
        Assert.Equal(EvidenceStatus.Missing, evidence.Status);
    }

    // -------------------------------------------------------------------------
    // Scenario 3: Grok runner — crash/restart recovery path
    // Run-1 crashes (exit code 1) with a RetryRecord; run-2 recovers and succeeds.
    // Evidence is merged across both runs. The failed CommandRecord from run-1
    // remains visible, producing a Fix verdict even though run-2 succeeded.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Grok_CrashAndRestart_MergedBundleShowsRetryAndFixVerdict()
    {
        var (slug, ticketId) = await CreateProjectAndTicketAsync(
            "bench-s3-grok", "Add JWT refresh token rotation with Redis cache");

        var failed = await RunProviderAsync(slug, ticketId, CliProvider.Grok,
            "grok-4", "error-exit", initializeGit: true);
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        await WaitForEvidenceAsync(slug, ticketId, failed.RunId);

        // Restart the same ticket through the runner lifecycle with a fresh process/run id.
        var recovered = await RunProviderAsync(slug, ticketId, CliProvider.Grok,
            "grok-4", "default", initializeGit: true);
        Assert.Equal(AgentRunStatus.Completed, recovered.Status);
        var merged = await WaitForEvidenceAsync(slug, ticketId, recovered.RunId, expectedRunCount: 2);

        var ev = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/evidence");
        Assert.Equal("Complete", ev!["status"]!.GetValue<string>());
        Assert.Equal(2, ev["runIds"]!.AsArray().Count);
        Assert.Contains(ev["commandsRun"]!.AsArray(), c => c!["exitCode"]!.GetValue<int>() == 1);
        Assert.Contains(ev["commandsRun"]!.AsArray(), c => c!["exitCode"]!.GetValue<int>() == 0);
        Assert.All(ev["commandsRun"]!.AsArray(), c =>
            Assert.Equal("grok", c!["provenance"]!["provider"]!.GetValue<string>()));

        var brief = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        // Fix: the failed CommandRecord from run-1 is a blocking finding
        Assert.Equal("Fix", brief!["verdict"]!.GetValue<string>());
        Assert.Equal("Complete", brief["evidenceStatus"]!.GetValue<string>());

        var findings = brief["findings"]!.AsArray();
        var crashFinding = findings.FirstOrDefault(f => f!["category"]!.GetValue<string>() == "command-failure");
        Assert.NotNull(crashFinding);
        Assert.True(crashFinding!["isBlocking"]!.GetValue<bool>());

        Assert.Contains(failed.RunId, merged.RunIds);
        Assert.Contains(recovered.RunId, merged.RunIds);
    }

    // -------------------------------------------------------------------------
    // Adversarial: no evidence seeded → both endpoints return 404
    // The EvidenceStore is keyed by ticket ID alone (no project prefix), so earlier
    // tests may have written ticket-1.evidence.json.  We use a second ticket (ID=2)
    // within the same project to guarantee a clean slot.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NoEvidence_BothEndpointsReturn404()
    {
        // First ticket (ID=1) may collide with earlier evidence files in the shared store.
        var (slug, _) = await CreateProjectAndTicketAsync("bench-s4-empty", "Scratch ticket");

        // Second ticket (ID=2) — no evidence is ever seeded for this ID in the suite.
        var ticketResp = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new { title = "Ticket with no evidence yet", createdBy = "qa-tester", status = "Todo", priority = "NiceToHave" });
        ticketResp.EnsureSuccessStatusCode();
        var ticket = JsonNode.Parse(await ticketResp.Content.ReadAsStringAsync())!;
        var ticketId2 = ticket["id"]!.GetValue<int>();

        var evidenceResp = await _client.GetAsync($"/api/projects/{slug}/tickets/{ticketId2}/evidence");
        Assert.Equal(HttpStatusCode.NotFound, evidenceResp.StatusCode);

        var briefResp = await _client.GetAsync($"/api/projects/{slug}/tickets/{ticketId2}/brief");
        Assert.Equal(HttpStatusCode.NotFound, briefResp.StatusCode);
    }

    [Fact]
    public async Task EvidenceEndpoints_SameIdentifiersAcrossProjects_ReturnOnlyScopedEvidence()
    {
        var (firstSlug, firstTicketId) = await CreateProjectAndTicketAsync(
            "bench-isolation-a", "First project evidence");
        var (secondSlug, secondTicketId) = await CreateProjectAndTicketAsync(
            "bench-isolation-b", "Second project evidence");
        Assert.Equal(firstTicketId, secondTicketId);

        const string sharedRunId = "shared-run-id";
        var first = MakeBundle(firstSlug, firstTicketId, sharedRunId, "claude");
        var second = MakeBundle(secondSlug, secondTicketId, sharedRunId, "codex");
        var store = _factory.Services.GetRequiredService<EvidenceStore>();
        store.MergeAndSave(first);
        store.MergeAndSave(second);

        var firstTicket = await GetJsonAsync($"/api/projects/{firstSlug}/tickets/{firstTicketId}/evidence");
        var secondTicket = await GetJsonAsync($"/api/projects/{secondSlug}/tickets/{secondTicketId}/evidence");
        Assert.Equal("claude", firstTicket!["commandsRun"]![0]!["provenance"]!["provider"]!.GetValue<string>());
        Assert.Equal("codex", secondTicket!["commandsRun"]![0]!["provenance"]!["provider"]!.GetValue<string>());

        var firstRun = await GetJsonAsync($"/api/projects/{firstSlug}/runs/{sharedRunId}/evidence");
        var secondRun = await GetJsonAsync($"/api/projects/{secondSlug}/runs/{sharedRunId}/evidence");
        Assert.Equal(firstSlug, firstRun!["projectSlug"]!.GetValue<string>());
        Assert.Equal(secondSlug, secondRun!["projectSlug"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // Adversarial: contradictory evidence → Block verdict, Reconcile guidance
    // Two verified captures disagree on the change kind for the same file.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ContradictoryEvidence_BriefVerdictIsBlock()
    {
        var (slug, ticketId) = await CreateProjectAndTicketAsync(
            "bench-s5-contra", "Ticket where two runs disagree on file changes");

        var provA = MakeProv("claude", "run-a");
        var provB = MakeProv("claude", "run-b");

        var evidence = new TicketEvidence
        {
            TicketId = ticketId.ToString(),
            ProjectSlug = slug,
            CapturedAt = DateTime.UtcNow,
        };
        evidence.RunIds.AddRange(["run-a", "run-b"]);
        // Same file, different change kinds — triggers Contradictory status
        evidence.ChangedFiles.Add(new ChangedFile("Shared/Config.cs", FileChangeKind.Added, null, provA));
        evidence.ChangedFiles.Add(new ChangedFile("Shared/Config.cs", FileChangeKind.Deleted, null, provB));
        evidence.Status = ProvenanceRules.ComputeStatus(evidence); // → Contradictory
        SeedEvidence(evidence);

        var brief = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        Assert.Equal("Block", brief!["verdict"]!.GetValue<string>());
        Assert.Equal("Contradictory", brief["evidenceStatus"]!.GetValue<string>());
        Assert.Equal("Reconcile", brief["recoveryGuidance"]!["action"]!.GetValue<string>());
        Assert.Contains("Shared/Config.cs", brief["recoveryGuidance"]!["reason"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<(string slug, int ticketId)> CreateProjectAndTicketAsync(string name, string title)
    {
        var projResp = await _client.PostAsJsonAsync("/api/projects", new { name });
        projResp.EnsureSuccessStatusCode();
        var proj = JsonNode.Parse(await projResp.Content.ReadAsStringAsync())!;
        var slug = proj["slug"]!.GetValue<string>();

        var ticketResp = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new { title, createdBy = "qa-tester", status = "Todo", priority = "NiceToHave" });
        ticketResp.EnsureSuccessStatusCode();
        var ticket = JsonNode.Parse(await ticketResp.Content.ReadAsStringAsync())!;
        return (slug, ticket["id"]!.GetValue<int>());
    }

    private async Task<AgentRun> RunProviderAsync(
        string slug,
        int ticketId,
        CliProvider provider,
        string model,
        string scenario,
        bool initializeGit)
    {
        var projects = _factory.Services.GetRequiredService<ProjectService>();
        var project = await projects.GetProjectAsync(slug) ?? throw new InvalidOperationException($"Project {slug} not found");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        if (initializeGit && !Directory.Exists(Path.Combine(workspace, ".git")))
        {
            await RunProcessAsync("git", "init", workspace);
            await RunProcessAsync("git", "config user.email benchmark@kittyclaw.test", workspace);
            await RunProcessAsync("git", "config user.name KittyClaw Benchmark", workspace);
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "# Benchmark fixture\n");
            await RunProcessAsync("git", "add .", workspace);
            await RunProcessAsync("git", "commit -m fixture", workspace);
        }

        TestSkillBuilder.Create(workspace, "benchmark-agent", scenario);
        if (initializeGit)
        {
            await RunProcessAsync("git", "add .", workspace);
            await RunProcessAsync("git", "commit -m skill", workspace);
        }

        var mockBin = Environment.GetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN");
        Assert.False(string.IsNullOrWhiteSpace(mockBin), "MockClaude fixture did not resolve the mock binary");
        var binVariable = provider switch
        {
            CliProvider.Codex => "KITTYCLAW_CODEX_BIN",
            CliProvider.Grok => "KITTYCLAW_GROK_BIN",
            _ => "KITTYCLAW_CLAUDE_BIN",
        };
        var previousBin = Environment.GetEnvironmentVariable(binVariable);
        Environment.SetEnvironmentVariable(binVariable, mockBin);
        CodexCli.ResetForTests();
        GrokCli.ResetForTests();
        try
        {
            var runs = _factory.Services.GetRequiredService<AgentRunRegistry>();
            var runner = new AgentRunner(
                new SessionRegistry(), runs, new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance);
            return await runner.RunAsync(new AgentRunContext
            {
                ProjectSlug = slug,
                WorkspacePath = workspace,
                AgentName = "benchmark-agent",
                SkillFile = "benchmark-agent/SKILL.md",
                TicketId = ticketId,
                TicketTitle = "Evidence benchmark",
                MaxTurns = 1,
                Model = model,
                Provider = provider,
                Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIO"] = scenario },
                PersistSession = false,
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            Environment.SetEnvironmentVariable(binVariable, previousBin);
            CodexCli.ResetForTests();
            GrokCli.ResetForTests();
        }
    }

    private async Task<TicketEvidence> WaitForEvidenceAsync(
        string slug, int ticketId, string runId, int expectedRunCount = 1)
    {
        var store = _factory.Services.GetRequiredService<EvidenceStore>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var evidence = store.LoadTicket(slug, ticketId.ToString());
            if (evidence is not null && evidence.RunIds.Contains(runId) && evidence.RunIds.Count >= expectedRunCount)
                return evidence;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Evidence for run {runId} was not attached within 10 seconds");
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        var result = await ProcessRunner.RunAsync(fileName, arguments, workingDirectory, TimeSpan.FromSeconds(10));
        Assert.True(result.Success, $"{fileName} {arguments} failed: {result.Stderr}");
    }

    private void SeedEvidence(TicketEvidence evidence)
    {
        var store = _factory.Services.GetRequiredService<EvidenceStore>();
        store.SaveTicket(evidence);
    }

    private async Task<JsonNode?> GetJsonAsync(string path)
    {
        var resp = await _client.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static EvidenceProvenance MakeProv(string provider, string runId) =>
        new("process-exit", provider, runId, DateTime.UtcNow, EvidenceTrust.Verified);

    private static TicketEvidence MakeBundle(string slug, int ticketId, string runId, string provider)
    {
        var provenance = MakeProv(provider, runId);
        var evidence = new TicketEvidence
        {
            TicketId = ticketId.ToString(),
            ProjectSlug = slug,
            CapturedAt = DateTime.UtcNow,
        };
        evidence.RunIds.Add(runId);
        evidence.CommandsRun.Add(new CommandRecord(
            provider, "/workspace", 0, true, "status=Completed", null, null, provenance));
        evidence.Status = ProvenanceRules.ComputeStatus(evidence);
        return evidence;
    }

    // -------------------------------------------------------------------------
    // Test host factory — isolated throwaway data dir, no shared state.
    // -------------------------------------------------------------------------

    public sealed class BenchmarkFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public BenchmarkFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kc-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _dataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}
