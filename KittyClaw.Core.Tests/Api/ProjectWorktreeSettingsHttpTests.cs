using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Core.Tests.Services;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Api;

public sealed class ProjectWorktreeSettingsHttpTests : IClassFixture<ProjectWorktreeSettingsHttpTests.ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ProjectWorktreeSettingsHttpTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Patch_ExposesAndUpdatesWorktreeSettings()
    {
        var repository = ProjectWorktreeSettingsTests.CreateRepository(_factory.DataDir, "dev");
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("api-worktrees"));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();
        (await _client.PatchAsJsonAsync($"/api/projects/{slug}", new { workspacePath = repository })).EnsureSuccessStatusCode();

        var patch = await _client.PatchAsJsonAsync($"/api/projects/{slug}",
            new { worktreesEnabled = true, integrationBranch = "dev" });

        patch.EnsureSuccessStatusCode();
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("worktreesEnabled").GetBoolean());
        Assert.Equal("dev", body.GetProperty("integrationBranch").GetString());
        Assert.Equal(repository, body.GetProperty("workspacePath").GetString());
    }

    [Fact]
    public async Task Patch_InvalidRepositoryReturnsBadRequest()
    {
        var create = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("api-invalid-worktrees"));
        create.EnsureSuccessStatusCode();
        var slug = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();

        var patch = await _client.PatchAsJsonAsync($"/api/projects/{slug}",
            new { worktreesEnabled = true, integrationBranch = "dev" });

        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Contains("dépôt Git", await patch.Content.ReadAsStringAsync());
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        public string DataDir { get; } = Path.Combine(
            Path.GetTempPath(), "kittyclaw-project-worktrees-" + Guid.NewGuid().ToString("N"));

        public ApiFactory()
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", DataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(DataDir, recursive: true); } catch { }
        }
    }
}
