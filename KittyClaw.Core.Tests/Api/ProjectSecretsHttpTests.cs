using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KittyClaw.Core.Tests.Api;

public sealed class ProjectSecretsHttpTests : IClassFixture<ProjectSecretsHttpTests.ApiFactory>
{
    private readonly HttpClient _client;

    public ProjectSecretsHttpTests(ApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Api_is_write_only_and_never_returns_saved_value()
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Secret API"));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();
        const string secretValue = "http-secret-value-278";

        var save = await _client.PutAsJsonAsync($"/api/projects/{slug}/secrets/API_TOKEN", new { value = secretValue });
        save.EnsureSuccessStatusCode();
        var saveBody = await save.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secretValue, saveBody);
        Assert.DoesNotContain("value", saveBody, StringComparison.OrdinalIgnoreCase);

        var listBody = await _client.GetStringAsync($"/api/projects/{slug}/secrets");
        Assert.Contains("API_TOKEN", listBody);
        Assert.DoesNotContain(secretValue, listBody);
        Assert.DoesNotContain("\"value\"", listBody, StringComparison.OrdinalIgnoreCase);

        var reveal = await _client.GetAsync($"/api/projects/{slug}/secrets/API_TOKEN");
        Assert.Equal(HttpStatusCode.NotFound, reveal.StatusCode);

        var replace = await _client.PutAsJsonAsync($"/api/projects/{slug}/secrets/API_TOKEN", new { value = "replacement" });
        replace.EnsureSuccessStatusCode();
        var delete = await _client.DeleteAsync($"/api/projects/{slug}/secrets/API_TOKEN");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal("[]", await _client.GetStringAsync($"/api/projects/{slug}/secrets"));
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        private readonly string _dataDir = Path.Combine(
            Path.GetTempPath(), "kittyclaw-secrets-api-" + Guid.NewGuid().ToString("N"));

        public ApiFactory()
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(Path.Combine(_dataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", _dataDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProjectSecretVault>();
                services.AddSingleton(new ProjectSecretVault(_dataDir, new TestSecretProtector()));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(_dataDir, recursive: true); } catch { }
        }
    }
}
