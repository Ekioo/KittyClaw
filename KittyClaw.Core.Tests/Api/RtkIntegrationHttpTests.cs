using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

public sealed class RtkIntegrationHttpTests : IClassFixture<RtkIntegrationHttpTests.ApiFactory>
{
    private readonly HttpClient _client;

    public RtkIntegrationHttpTests(ApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Api_ExposesOptInAndTruthfulUnavailableFallback()
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("rtk-api"));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();

        var initial = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/rtk");
        Assert.False(initial.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", initial.GetProperty("mode").GetString());

        var patch = await _client.PatchAsJsonAsync($"/api/projects/{slug}/rtk", new { enabled = true });
        patch.EnsureSuccessStatusCode();
        var status = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.Equal("unavailable", status.GetProperty("mode").GetString());

        var project = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}");
        Assert.True(project.GetProperty("rtkEnabled").GetBoolean());
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir = Path.Combine(
            Path.GetTempPath(), "kittyclaw-rtk-api-" + Guid.NewGuid().ToString("N"));
        private readonly string _missingBinary = Path.Combine(
            Path.GetTempPath(), "kittyclaw-missing-rtk-" + Guid.NewGuid().ToString("N"));

        public ApiFactory()
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _dataDir);
            Environment.SetEnvironmentVariable("KITTYCLAW_RTK_BIN", _missingBinary);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            Environment.SetEnvironmentVariable("KITTYCLAW_RTK_BIN", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}
