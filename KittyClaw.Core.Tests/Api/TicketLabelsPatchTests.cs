using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// Incremental ticket-label patch (backport analysis §2.3). The replace-all primitive
/// invites the lost update: two writers each read [bug], each send back "their" full
/// list, last one wins and the other's label vanishes — and the MCP client's add_label
/// even wiped ALL labels this way (§4.1). PATCH .../labels merges add/remove by name
/// server-side, so concurrent writers can never overwrite each other.
/// </summary>
public sealed class TicketLabelsPatchTests : IClassFixture<TicketLabelsPatchTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;

    public TicketLabelsPatchTests(ApiFactory factory) => _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    private async Task<(string Slug, int TicketId)> CreateProjectWithTicketAsync()
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("labels-patch-" + Guid.NewGuid().ToString("N")[..8]));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var ticketResp = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new CreateTicketRequest("Labeled ticket", "owner", "Todo"));
        ticketResp.EnsureSuccessStatusCode();
        var ticketId = (await ticketResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        return (slug, ticketId);
    }

    private async Task<List<string>> LabelNamesAsync(string slug, int id)
    {
        var labels = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}/labels");
        return labels.EnumerateArray().Select(l => l.GetProperty("name").GetString()!).ToList();
    }

    [Fact]
    public async Task Add_CreatesMissingLabel_AndAppendsWithoutTouchingOthers()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        var first = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/labels",
            new { author = "programmer", add = new[] { "bug" } });
        first.EnsureSuccessStatusCode();

        // The MCP §4.1 destructive scenario, inverted: a second independent add must
        // KEEP the first label instead of replacing the collection.
        var second = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/labels",
            new { author = "qa-tester", add = new[] { "urgent" } });
        second.EnsureSuccessStatusCode();

        var names = await LabelNamesAsync(slug, id);
        Assert.Contains("bug", names);
        Assert.Contains("urgent", names);
    }

    [Fact]
    public async Task Remove_RemovesOnlyTheNamedLabel_UnknownRemoveIsANoOp()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();
        (await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/labels",
            new { author = "owner", add = new[] { "bug", "urgent" } })).EnsureSuccessStatusCode();

        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/labels",
            new { author = "owner", remove = new[] { "bug", "never-existed" } });
        resp.EnsureSuccessStatusCode();

        var names = await LabelNamesAsync(slug, id);
        Assert.DoesNotContain("bug", names);
        Assert.Contains("urgent", names);
    }

    [Fact]
    public async Task ConcurrentAdds_AllSurvive()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();
        var labels = Enumerable.Range(1, 8).Select(i => $"tag-{i}").ToList();

        // The lost-update scenario at full strength: 8 concurrent writers, one label each.
        // With replace-all semantics most of them would vanish; the merge keeps them all.
        await Task.WhenAll(labels.Select(name =>
            _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/labels",
                new { author = "owner", add = new[] { name } })));

        var names = await LabelNamesAsync(slug, id);
        foreach (var name in labels)
            Assert.Contains(name, names);
    }

    [Fact]
    public async Task EmptyPatch_Returns400()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        var resp = await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/labels",
            new { author = "owner" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_LogsAnActivity()
    {
        var (slug, id) = await CreateProjectWithTicketAsync();

        (await _client.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{id}/labels",
            new { author = "programmer", add = new[] { "bug" }, remove = new[] { "wontfix" } })).EnsureSuccessStatusCode();

        var ticket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Contains(
            ticket.GetProperty("activities").EnumerateArray(),
            a => a.GetProperty("text").GetString()!.Contains("+bug")
              && a.GetProperty("author").GetString() == "programmer");
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-labels-patch-" + Guid.NewGuid().ToString("N"));
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
