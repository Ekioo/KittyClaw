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
