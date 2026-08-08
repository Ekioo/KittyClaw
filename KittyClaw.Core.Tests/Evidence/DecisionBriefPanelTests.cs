using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Core.Evidence;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Evidence;

/// <summary>
/// Tests for the GET /api/projects/{slug}/tickets/{id}/brief endpoint (ticket #160).
/// Each test class has its own isolated ApiFactory so EvidenceStore state cannot bleed
/// between tests (EvidenceStore is keyed by ticket ID only, not project slug).
/// Scenarios covered: complete evidence, missing, failed runs, stale, multiple runs,
/// contradictory evidence, and a ticket that does not exist.
/// </summary>
[Collection("Brief160Isolated")]
public sealed class DecisionBriefPanelTests : IClassFixture<DecisionBriefPanelTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiFactory _factory;

    public DecisionBriefPanelTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task BriefEndpoint_NonExistentTicket_Returns404()
    {
        var slug = await CreateProjectAsync("BriefNonExistent");

        var resp = await _client.GetAsync($"/api/projects/{slug}/tickets/9999/brief");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task BriefEndpoint_CompleteEvidence_ReturnsShip()
    {
        var slug = await CreateProjectAsync("BriefComplete");
        var ticketId = await CreateTicketAsync(slug, "T-Complete");
        SeedEvidence(ticketId.ToString(), slug, EvidenceStatus.Complete, commandSucceeded: true);

        var json = await _client.GetStringAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Ship", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("testsFailed").GetInt32());
    }

    [Fact]
    public async Task BriefEndpoint_MissingEvidence_ReturnsBlock()
    {
        var slug = await CreateProjectAsync("BriefMissing");
        var ticketId = await CreateTicketAsync(slug, "T-Missing");
        SeedEvidence(ticketId.ToString(), slug, EvidenceStatus.Missing, commandSucceeded: true);

        var json = await _client.GetStringAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Block", doc.RootElement.GetProperty("verdict").GetString());
        var recoveryAction = doc.RootElement.GetProperty("recoveryGuidance").GetProperty("action").GetString();
        Assert.Equal("Recapture", recoveryAction);
    }

    [Fact]
    public async Task BriefEndpoint_FailedRun_ReturnsFix()
    {
        var slug = await CreateProjectAsync("BriefFailed");
        var ticketId = await CreateTicketAsync(slug, "T-Failed");
        SeedEvidence(ticketId.ToString(), slug, EvidenceStatus.Complete,
            commandSucceeded: false, testsFailed: 3);

        var json = await _client.GetStringAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Fix", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("testsFailed").GetInt32());
        var findings = doc.RootElement.GetProperty("findings").EnumerateArray().ToList();
        Assert.Contains(findings, f => f.GetProperty("category").GetString() == "command-failure");
    }

    [Fact]
    public async Task BriefEndpoint_StaleEvidence_ReturnsBlock_WithRecaptureGuidance()
    {
        var slug = await CreateProjectAsync("BriefStale");
        var ticketId = await CreateTicketAsync(slug, "T-Stale");
        SeedEvidence(ticketId.ToString(), slug, EvidenceStatus.Stale, commandSucceeded: true);

        var json = await _client.GetStringAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Block", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("Recapture",
            doc.RootElement.GetProperty("recoveryGuidance").GetProperty("action").GetString());
    }

    [Fact]
    public async Task BriefEndpoint_MultipleRuns_AllRunIdsReturned()
    {
        var slug = await CreateProjectAsync("BriefMultiRun");
        var ticketId = await CreateTicketAsync(slug, "T-Multi");
        SeedEvidence(ticketId.ToString(), slug, EvidenceStatus.Complete,
            commandSucceeded: true, runIds: ["run-alpha", "run-beta"]);

        var json = await _client.GetStringAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        using var doc = JsonDocument.Parse(json);

        var runIds = doc.RootElement.GetProperty("runIds").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("run-alpha", runIds);
        Assert.Contains("run-beta", runIds);
    }

    [Fact]
    public async Task BriefEndpoint_ContradictoryEvidence_ReturnsReconcileGuidance()
    {
        var slug = await CreateProjectAsync("BriefContradictory");
        var ticketId = await CreateTicketAsync(slug, "T-Contradictory");
        SeedEvidence(ticketId.ToString(), slug, EvidenceStatus.Contradictory, commandSucceeded: true);

        var json = await _client.GetStringAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Block", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("Reconcile",
            doc.RootElement.GetProperty("recoveryGuidance").GetProperty("action").GetString());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private void SeedEvidence(
        string ticketId,
        string slug,
        EvidenceStatus status,
        bool commandSucceeded = true,
        int testsFailed = 0,
        string[]? runIds = null)
    {
        var store = new EvidenceStore(_factory.DataDir);
        var ids = runIds ?? ["run-1"];
        var evidence = new TicketEvidence
        {
            TicketId = ticketId,
            ProjectSlug = slug,
            CapturedAt = DateTime.UtcNow,
            Status = status,
        };
        foreach (var id in ids)
            evidence.RunIds.Add(id);
        var prov = new EvidenceProvenance("process-exit", "claude", ids[0], DateTime.UtcNow, EvidenceTrust.Verified);
        evidence.CommandsRun.Add(new CommandRecord(
            Command: "dotnet test",
            WorkingDirectory: "/ws",
            ExitCode: commandSucceeded ? 0 : 1,
            Success: commandSucceeded,
            OutputSummary: null,
            TestsPassed: 5,
            TestsFailed: testsFailed,
            Provenance: prov));
        store.SaveTicket(evidence);
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("slug").GetString()!;
    }

    private async Task<int> CreateTicketAsync(string slug, string title)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new { title, createdBy = "test", status = "Todo" });
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;
        public string DataDir => _dataDir;

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-brief160-" + Guid.NewGuid().ToString("N"));
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

/// <summary>
/// Separate fixture for the "no evidence captured yet" scenario so its
/// EvidenceStore is fully isolated from the seeded-evidence tests above.
/// </summary>
[Collection("Brief160NoEvidenceIsolated")]
public sealed class DecisionBriefPanel_NoEvidenceTests
    : IClassFixture<DecisionBriefPanel_NoEvidenceTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;

    public DecisionBriefPanel_NoEvidenceTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task BriefEndpoint_NoEvidenceSeeded_Returns404()
    {
        var slug = await CreateProjectAsync("BriefEmpty");
        var ticketId = await CreateTicketAsync(slug, "T-Empty");

        var resp = await _client.GetAsync($"/api/projects/{slug}/tickets/{ticketId}/brief");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("slug").GetString()!;
    }

    private async Task<int> CreateTicketAsync(string slug, string title)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new { title, createdBy = "test", status = "Todo" });
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt32();
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        public ApiFactory()
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-brief160-noev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", dataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
        }
    }
}
