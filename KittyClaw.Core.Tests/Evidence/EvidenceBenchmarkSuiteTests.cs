using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KittyClaw.Core.Evidence;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

        var prov = MakeProv("claude", "run-claude-1");
        var evidence = new TicketEvidence
        {
            TicketId = ticketId.ToString(),
            ProjectSlug = slug,
            CapturedAt = DateTime.UtcNow,
        };
        evidence.RunIds.Add("run-claude-1");
        evidence.ChangedFiles.Add(new ChangedFile("Middleware/RateLimiting.cs", FileChangeKind.Added, null, prov));
        evidence.CommandsRun.Add(new CommandRecord("dotnet test", "/ws", 0, true, "5 passed, 0 failed", 5, 0, prov));
        evidence.RepositoryState = new RepositoryState("ticket/42", "abc1234", true, [], prov);
        evidence.Status = ProvenanceRules.ComputeStatus(evidence);
        SeedEvidence(evidence);

        // Evidence endpoint: status = Complete, provider = claude
        var ev = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/evidence");
        Assert.Equal("Complete", ev!["status"]!.GetValue<string>());
        Assert.Equal("claude", ev["commandsRun"]![0]!["provenance"]!["provider"]!.GetValue<string>());

        // Brief endpoint: Ship verdict, all fields present
        var brief = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        Assert.Equal("Ship", brief!["verdict"]!.GetValue<string>());
        Assert.Equal("Complete", brief["evidenceStatus"]!.GetValue<string>());
        Assert.Equal(1, brief["filesChanged"]!.GetValue<int>());
        Assert.Equal(1, brief["commandsRun"]!.GetValue<int>());
        Assert.Equal(5, brief["testsPassed"]!.GetValue<int>());
        Assert.Equal(0, brief["testsFailed"]!.GetValue<int>());
        Assert.True(brief["repositoryClean"]!.GetValue<bool>());
        Assert.Contains(brief["runIds"]!.AsArray(), n => n!.GetValue<string>() == "run-claude-1");
        Assert.DoesNotContain(brief["findings"]!.AsArray(), f => f!["isBlocking"]!.GetValue<bool>());
        Assert.Equal("None", brief["recoveryGuidance"]!["action"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // Scenario 2: Codex runner — partial-evidence path
    // One file item carries AgentClaim trust (model-asserted, not git-verified).
    // Status = Partial; verdict = Ship with a non-blocking agent-claim finding.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Codex_PartialEvidence_NonBlockingFindingAndFixVerdict()
    {
        var (slug, ticketId) = await CreateProjectAndTicketAsync(
            "bench-s2-codex", "Refactor database connection pool to use async/await pattern");

        var verified = MakeProv("codex", "run-codex-1");
        var claim = new EvidenceProvenance(
            "agent-output", "codex", "run-codex-1", DateTime.UtcNow, EvidenceTrust.AgentClaim);

        var evidence = new TicketEvidence
        {
            TicketId = ticketId.ToString(),
            ProjectSlug = slug,
            CapturedAt = DateTime.UtcNow,
        };
        evidence.RunIds.Add("run-codex-1");
        // Git-captured file: Verified
        evidence.ChangedFiles.Add(new ChangedFile("Data/ConnectionPool.cs", FileChangeKind.Modified, null, verified));
        // Agent-claimed file: model said it wrote tests, but git did not capture the new file
        evidence.ChangedFiles.Add(new ChangedFile("Tests/ConnectionPoolTests.cs", FileChangeKind.Added, null, claim));
        evidence.CommandsRun.Add(new CommandRecord("codex", "/ws", 0, true, "status=Completed", null, null, verified));
        evidence.RepositoryState = new RepositoryState(
            "ticket/42", "bcd2345", false, ["Tests/ConnectionPoolTests.cs"], verified);
        evidence.Status = ProvenanceRules.ComputeStatus(evidence); // → Partial
        SeedEvidence(evidence);

        var ev = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/evidence");
        Assert.Equal("Partial", ev!["status"]!.GetValue<string>());

        var brief = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        Assert.Equal("Fix", brief!["verdict"]!.GetValue<string>());
        Assert.Equal("Partial", brief["evidenceStatus"]!.GetValue<string>());

        var findings = brief["findings"]!.AsArray();
        var agentClaim = findings.FirstOrDefault(f => f!["category"]!.GetValue<string>() == "agent-claim");
        Assert.NotNull(agentClaim);
        Assert.False(agentClaim!["isBlocking"]!.GetValue<bool>());

        // Partial → Recapture guidance
        Assert.Equal("Recapture", brief["recoveryGuidance"]!["action"]!.GetValue<string>());
        Assert.Contains("agent claim", brief["recoveryGuidance"]!["reason"]!.GetValue<string>());
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

        var prov1 = MakeProv("grok", "run-grok-1");
        var prov2 = MakeProv("grok", "run-grok-2");

        // Merged bundle — simulates EvidenceStore.MergeAndSave across two distinct runs
        var merged = new TicketEvidence
        {
            TicketId = ticketId.ToString(),
            ProjectSlug = slug,
            CapturedAt = DateTime.UtcNow,
        };
        merged.RunIds.AddRange(["run-grok-1", "run-grok-2"]);

        // Run 1: crashed — preserved as a failed CommandRecord in the merged bundle
        merged.CommandsRun.Add(new CommandRecord("grok", "/ws", 1, false, "status=Failed", null, null, prov1));
        merged.Retries.Add(new RetryRecord(1, "Process exited with code 1 — crash detected; restarting", prov1));

        // Run 2: recovered and succeeded
        merged.CommandsRun.Add(new CommandRecord("grok", "/ws", 0, true, "status=Completed", 12, 0, prov2));
        merged.ChangedFiles.Add(new ChangedFile("Auth/JwtRefreshService.cs", FileChangeKind.Added, null, prov2));
        merged.RepositoryState = new RepositoryState("ticket/42", "cde3456", true, [], prov2);
        merged.Status = ProvenanceRules.ComputeStatus(merged); // → Complete (all Verified)
        SeedEvidence(merged);

        var ev = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/evidence");
        Assert.Equal("Complete", ev!["status"]!.GetValue<string>());
        Assert.Equal(2, ev["runIds"]!.AsArray().Count);
        Assert.True(ev["retries"]!.AsArray().Count > 0, "RetryRecord must be present for the crash");

        var brief = await GetJsonAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        // Fix: the failed CommandRecord from run-1 is a blocking finding
        Assert.Equal("Fix", brief!["verdict"]!.GetValue<string>());
        Assert.Equal("Complete", brief["evidenceStatus"]!.GetValue<string>());

        var findings = brief["findings"]!.AsArray();
        var crashFinding = findings.FirstOrDefault(f => f!["category"]!.GetValue<string>() == "command-failure");
        Assert.NotNull(crashFinding);
        Assert.True(crashFinding!["isBlocking"]!.GetValue<bool>());

        // Tests from run-2 are counted in the brief
        Assert.Equal(12, brief["testsPassed"]!.GetValue<int>());
        Assert.Equal(0, brief["testsFailed"]!.GetValue<int>());
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
