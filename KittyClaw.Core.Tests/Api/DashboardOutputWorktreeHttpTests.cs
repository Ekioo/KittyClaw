using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KittyClaw.Core.Tests.Api;

public sealed class DashboardOutputWorktreeHttpTests : IClassFixture<DashboardOutputWorktreeHttpTests.ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public DashboardOutputWorktreeHttpTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PutOutput_UsesMaintenanceWorktree_WhenProjectWorktreesAreEnabled()
    {
        var repository = ProjectWorktreeSettingsTests.CreateRepository(_factory.DataDir, "dev");
        var tileDirectory = Path.Combine(repository, ".dashboard", "roadmap");
        Directory.CreateDirectory(tileDirectory);
        await File.WriteAllTextAsync(Path.Combine(tileDirectory, "tile.yaml"), "template: markdown\nrefresh: 0\n");
        await File.WriteAllTextAsync(Path.Combine(tileDirectory, "output.md"), "before");
        Git(repository, "add", ".dashboard/roadmap");
        Git(repository, "commit", "-m", "add roadmap tile");

        var created = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("dashboard-output-" + Guid.NewGuid().ToString("N")));
        created.EnsureSuccessStatusCode();
        var slug = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        (await _client.PatchAsJsonAsync($"/api/projects/{slug}", new { workspacePath = repository }))
            .EnsureSuccessStatusCode();
        (await _client.PatchAsJsonAsync($"/api/projects/{slug}", new
        {
            worktreesEnabled = true,
            integrationBranch = "dev",
            repositoryPath = repository
        })).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/api/projects/{slug}/pause", null)).EnsureSuccessStatusCode();

        using var content = new StringContent("after", Encoding.UTF8, "text/plain");
        var response = await _client.PutAsync(
            $"/api/projects/{slug}/dashboard/tiles/roadmap/output", content);

        Assert.True(response.IsSuccessStatusCode,
            $"Dashboard output returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(tileDirectory, "output.md")));
        var requests = await _client.GetFromJsonAsync<List<WorktreeMergeRequest>>(
            $"/api/projects/{slug}/worktree-merges", new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new JsonStringEnumConverter() } });
        var request = Assert.Single(requests!);
        Assert.Equal(WorktreeMergeJobKind.Maintenance, request.JobKind);
        Assert.Equal(WorktreeMergeStatus.Pending, request.Status);
        Assert.Equal($"maintenance/{slug}", request.SourceBranch);
        var maintenance = request.WorktreePath;
        Assert.Equal("after", await File.ReadAllTextAsync(
            Path.Combine(maintenance, ".dashboard", "roadmap", "output.md")));
        Assert.Empty(Git(repository, "status", "--porcelain"));
    }

    private static string Git(string path, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        public string DataDir { get; } = Path.Combine(
            Path.GetTempPath(), "kittyclaw-dashboard-output-" + Guid.NewGuid().ToString("N"));

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
