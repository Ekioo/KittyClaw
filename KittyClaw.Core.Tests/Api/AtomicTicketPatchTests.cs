using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// The root ticket PATCH is the atomic hand-off primitive (backport analysis §2.2,
/// follow-up to kittyclaw-front#113): every provided field — status included — applies in
/// ONE write, through the same semantics as the dedicated /status endpoint, so the
/// automation engine can never observe a transition half-applied. expectedStatus adds
/// optimistic concurrency (409 when the ticket already moved on), and unknown body fields
/// are rejected with a 400 naming the field instead of being silently dropped — the #113
/// failure mode, generalized away for every endpoint.
/// </summary>
public sealed class AtomicTicketPatchTests : IClassFixture<AtomicTicketPatchTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;

    public AtomicTicketPatchTests(ApiFactory factory) => _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private async Task<(string Slug, int TicketId)> CreateProjectWithTicketAsync()
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("atomic-patch-" + Guid.NewGuid().ToString("N")[..8]));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var ticketResp = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new CreateTicketRequest("Original title", "owner", "InProgress"));
        ticketResp.EnsureSuccessStatusCode();
        var ticketId = (await ticketResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        return (slug, ticketId);
    }

    private async Task<string> CreateMemberAsync(string slug, string name)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{slug}/members", new CreateMemberRequest(name));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
    }

    // ── The atomic hand-off ──────────────────────────────────────────────────

    [Fact]
    public async Task RootPatch_StatusAndAssignee_ApplyInOneCall()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();
        var member = await CreateMemberAsync(slug, "Programmer");

        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "qa-tester", status = "Todo", assignedTo = member });

        resp.EnsureSuccessStatusCode();
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("Todo", ticket.GetProperty("status").GetString());
        Assert.Equal(member, ticket.GetProperty("assignedTo").GetString());
        // The move is a real move: activity logged like the dedicated endpoint would.
        Assert.Contains(
            ticket.GetProperty("activities").EnumerateArray(),
            a => a.GetProperty("text").GetString()!.Contains("InProgress → Todo"));
    }

    [Fact]
    public async Task RootPatch_TheExactProdPayloadFrom113_NowActuallyMovesTheTicket()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        // The payload that was a silent 200 no-op, then an explicit 400: it is now simply
        // what the agent always meant — a real, validated, signaled move.
        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "lain", status = "Done" });

        resp.EnsureSuccessStatusCode();
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("Done", ticket.GetProperty("status").GetString());
        Assert.Contains(
            ticket.GetProperty("activities").EnumerateArray(),
            a => a.GetProperty("text").GetString()!.Contains("InProgress → Done"));
    }

    [Fact]
    public async Task RootPatch_UnknownColumn_Returns400_AndNothingApplies()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "owner", status = "NoSuchColumn", title = "Should not apply" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("InProgress", ticket.GetProperty("status").GetString());
        Assert.Equal("Original title", ticket.GetProperty("title").GetString());
    }

    // ── Optimistic concurrency ───────────────────────────────────────────────

    [Fact]
    public async Task RootPatch_ExpectedStatusMismatch_Returns409_AndNothingApplies()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        // Another agent already moved the ticket on: the conditional update must lose loudly.
        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "qa-tester", status = "Todo", expectedStatus = "Review", title = "Should not apply" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InProgress", body.GetProperty("actualStatus").GetString());
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("InProgress", ticket.GetProperty("status").GetString());
        Assert.Equal("Original title", ticket.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RootPatch_ExpectedStatusMatch_Applies()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "qa-tester", status = "Todo", expectedStatus = "InProgress" });

        resp.EnsureSuccessStatusCode();
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("Todo", ticket.GetProperty("status").GetString());
    }

    // ── Strict body validation (the #113 class, generalized) ─────────────────

    [Fact]
    public async Task RootPatch_UnknownField_Returns400_NamingTheField()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        // Typo'd field ("stauts"): used to be silently dropped with a 200 — the exact
        // failure mode that kept a prod ticket looping for 30 minutes.
        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "owner", stauts = "Done" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("stauts", error);
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("InProgress", ticket.GetProperty("status").GetString());
    }

    // ── Regressions ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RootPatch_WithoutStatus_StillUpdatesFields()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "owner", title = "Renamed title" });

        resp.EnsureSuccessStatusCode();
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("Renamed title", ticket.GetProperty("title").GetString());
        Assert.Equal("InProgress", ticket.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DedicatedStatusEndpoint_StillMovesTheTicket()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/status",
            new MoveTicketRequest("Done", "lain"));

        resp.EnsureSuccessStatusCode();
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("Done", ticket.GetProperty("status").GetString());
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-atomic-patch-" + Guid.NewGuid().ToString("N"));
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
