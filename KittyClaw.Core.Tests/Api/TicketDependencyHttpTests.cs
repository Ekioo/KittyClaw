using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KittyClaw.Core.Tests.Api;

public sealed class TicketDependencyHttpTests : IClassFixture<TicketDependencyHttpTests.ApiFactory>, IDisposable
{
    private readonly HttpClient _client;

    public TicketDependencyHttpTests(ApiFactory factory) => _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Create_ValidDependency_IsVisibleFromBothTickets()
    {
        var slug = await CreateProjectAsync();
        var blocked = await CreateTicketAsync(slug, "Blocked");
        var blocker = await CreateTicketAsync(slug, "Blocker");

        var create = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets/{blocked}/dependencies",
            new { blockedById = blocker });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var blockedTicket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{blocked}");
        var blockerTicket = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{blocker}");
        Assert.Equal(blocker, blockedTicket.GetProperty("blockedBy")[0].GetProperty("ticketId").GetInt32());
        Assert.Equal(blocked, blockerTicket.GetProperty("blocks")[0].GetProperty("ticketId").GetInt32());
    }

    [Fact]
    public async Task Create_InvalidDependencies_ReturnMachineReadableReasons()
    {
        var slug = await CreateProjectAsync();
        var ticketA = await CreateTicketAsync(slug, "A");
        var ticketB = await CreateTicketAsync(slug, "B");

        await AssertRejectedAsync(slug, ticketA, ticketA, "self_reference");
        await AssertRejectedAsync(slug, ticketA, int.MaxValue, "missing_ticket");

        var first = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets/{ticketA}/dependencies",
            new { blockedById = ticketB });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertRejectedAsync(slug, ticketA, ticketB, "duplicate_edge");
        await AssertRejectedAsync(slug, ticketB, ticketA, "cycle");
    }

    [Fact]
    public async Task Create_DependencyUsingTicketFromAnotherProject_IsRejected()
    {
        var slug = await CreateProjectAsync();
        var otherSlug = await CreateProjectAsync();
        var blocked = await CreateTicketAsync(slug, "Blocked");
        _ = await CreateTicketAsync(otherSlug, "Foreign id reservation");
        var foreign = await CreateTicketAsync(otherSlug, "Foreign blocker");

        await AssertRejectedAsync(slug, blocked, foreign, "missing_ticket");
    }

    [Fact]
    public async Task ConcurrentOppositeCreates_ExactlyOneSucceedsWithoutPersistingCycle()
    {
        var slug = await CreateProjectAsync();
        var ticketA = await CreateTicketAsync(slug, "A");
        var ticketB = await CreateTicketAsync(slug, "B");

        using var start = new ManualResetEventSlim(false);
        async Task<HttpResponseMessage> CreateAsync(int blocked, int blocker)
        {
            start.Wait();
            return await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets/{blocked}/dependencies",
                new { blockedById = blocker });
        }

        var first = Task.Run(() => CreateAsync(ticketA, ticketB));
        var second = Task.Run(() => CreateAsync(ticketB, ticketA));
        start.Set();
        var responses = await Task.WhenAll(first, second);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var rejection = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        var rejectionBody = await rejection.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cycle", rejectionBody.GetProperty("reason").GetString());

        var fetchedA = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{ticketA}");
        var fetchedB = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{ticketB}");
        var persistedEdges = fetchedA.GetProperty("blockedBy").GetArrayLength()
            + fetchedB.GetProperty("blockedBy").GetArrayLength();
        Assert.Equal(1, persistedEdges);
    }

    [Fact]
    public async Task ConcurrentDeletes_OneSucceedsAndOneReturnsStructuredNotFound()
    {
        var slug = await CreateProjectAsync();
        var blocked = await CreateTicketAsync(slug, "Blocked");
        var blocker = await CreateTicketAsync(slug, "Blocker");
        var create = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets/{blocked}/dependencies",
            new { blockedById = blocker });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dependencyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var path = $"/api/projects/{slug}/tickets/{blocked}/dependencies/{dependencyId}";

        using var start = new ManualResetEventSlim(false);
        async Task<HttpResponseMessage> DeleteAsync()
        {
            start.Wait();
            return await _client.DeleteAsync(path);
        }

        var first = Task.Run(DeleteAsync);
        var second = Task.Run(DeleteAsync);
        start.Set();
        var responses = await Task.WhenAll(first, second);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        var missing = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NotFound);
        var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dependency_not_found", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Delete_MissingOrWrongTicket_ReturnsStructuredNotFound()
    {
        var slug = await CreateProjectAsync();

        var blocked = await CreateTicketAsync(slug, "Blocked");
        var blocker = await CreateTicketAsync(slug, "Blocker");
        var unrelated = await CreateTicketAsync(slug, "Unrelated");

        var create = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets/{blocked}/dependencies",
            new { blockedById = blocker });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dependencyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var firstDelete = await _client.DeleteAsync(
            $"/api/projects/{slug}/tickets/{blocked}/dependencies/{dependencyId}");
        Assert.Equal(HttpStatusCode.NoContent, firstDelete.StatusCode);

        await AssertStructuredNotFoundAsync(
            $"/api/projects/{slug}/tickets/{blocked}/dependencies/{dependencyId}");
        await AssertStructuredNotFoundAsync(
            $"/api/projects/{slug}/tickets/{unrelated}/dependencies/{dependencyId}");
    }

    private async Task<int> CreateTicketAsync(string slug, string title)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets",
            new CreateTicketRequest(title, "owner", "Todo"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<string> CreateProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("dependency-http-" + Guid.NewGuid().ToString("N")[..8]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
    }

    private async Task AssertRejectedAsync(string slug, int blocked, int blocker, string reason)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{slug}/tickets/{blocked}/dependencies",
            new { blockedById = blocker });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reason, body.GetProperty("reason").GetString());
    }

    private async Task AssertStructuredNotFoundAsync(string path)
    {
        var response = await _client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dependency_not_found", body.GetProperty("reason").GetString());
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir;

        public ApiFactory()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "kittyclaw-dependency-http-" + Guid.NewGuid().ToString("N"));
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
