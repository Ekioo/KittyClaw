using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Regression tests for the root ticket-update endpoint silently dropping the "status" field
/// (diagnosed in prod on kittyclaw-front#113: five "restore Done" PATCH calls returned 200
/// as no-ops, keeping a ticket stuck in an automation loop). The root PATCH must now reject
/// payloads containing "status" with a 400 that points to the dedicated /status endpoint.
/// </summary>
public sealed class RootPatchStatusRejectionTests : IClassFixture<RootPatchStatusRejectionTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;

    public RootPatchStatusRejectionTests(ApiFactory factory) => _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private async Task<(string Slug, int TicketId)> CreateProjectWithTicketAsync()
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("patch-status-" + Guid.NewGuid().ToString("N")[..8]));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var ticketResp = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new CreateTicketRequest("Original title", "owner", "InProgress"));
        ticketResp.EnsureSuccessStatusCode();
        var ticketId = (await ticketResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        return (slug, ticketId);
    }

    [Fact]
    public async Task RootPatch_WithStatusField_Returns400_PointingToDedicatedEndpoint()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        // The exact prod payload shape that used to be a silent 200 no-op.
        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}",
            new { author = "lain", status = "Done" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains($"/tickets/{id}/status", error);

        // The ticket must not have moved, and no phantom activity logged.
        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("InProgress", ticket.GetProperty("status").GetString());
        Assert.DoesNotContain(
            ticket.GetProperty("activities").EnumerateArray(),
            a => a.GetProperty("text").GetString()!.Contains("Done"));
    }

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
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-patch-status-" + Guid.NewGuid().ToString("N"));
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
