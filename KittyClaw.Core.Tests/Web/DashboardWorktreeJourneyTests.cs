using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class DashboardWorktreeJourneyTests
{
    [DashboardWorktreeBrowserFact]
    public async Task OutputWrite_IntegratesSynchronizesAndRendersInTheRealDashboard()
    {
        var data = Path.Combine(Path.GetTempPath(), "kittyclaw-dashboard-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        var environment = new Dictionary<string, string?>
        {
            ["KITTYCLAW_DATA_DIR"] = data,
            ["KITTYCLAW_DESKTOP"] = "false",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0"
        };
        var previous = environment.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var factory = new JourneyFactory();
            factory.UseKestrel(0);
            using var http = factory.CreateClient();
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            // The live contract is served by this isolated application, never the main instance.
            var spec = await http.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
            Assert.True(spec.GetProperty("paths").TryGetProperty("/api/projects/{slug}/dashboard/tiles/{tileSlug}/output", out _));
            var repository = ProjectWorktreeSettingsTests.CreateRepository(data, "dev");
            var tile = Path.Combine(repository, ".dashboard", "roadmap");
            Directory.CreateDirectory(tile);
            await File.WriteAllTextAsync(Path.Combine(tile, "tile.yaml"), "template: markdown\ntitle: Worktree result\nrefresh: 0\n");
            await File.WriteAllTextAsync(Path.Combine(tile, "output.md"), "Before dashboard integration");
            Git(repository, "add", ".dashboard");
            Git(repository, "commit", "-m", "Initialize synthetic dashboard");
            var originalHead = Git(repository, "rev-parse", "HEAD");
            var created = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("dashboard-output-" + Guid.NewGuid().ToString("N")));
            created.EnsureSuccessStatusCode();
            var slug = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
            (await http.PatchAsJsonAsync($"/api/projects/{slug}", new { workspacePath = repository })).EnsureSuccessStatusCode();
            (await http.PatchAsJsonAsync($"/api/projects/{slug}", new
            { worktreesEnabled = true, integrationBranch = "dev", repositoryPath = repository })).EnsureSuccessStatusCode();
            (await http.PostAsync($"/api/projects/{slug}/pause", null)).EnsureSuccessStatusCode();

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            { Headless = true, Channel = Environment.GetEnvironmentVariable("KITTYCLAW_TEST_BROWSER_CHANNEL") });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            var url = new Uri(http.BaseAddress!, $"/board/{slug}/dashboard").ToString();
            await page.GotoAsync(url);
            var content = page.Locator(".dashboard-tile-content");
            await Expect(content).ToContainTextAsync("Before dashboard integration");

            using var body = new StringContent("After dashboard integration", Encoding.UTF8, "text/plain");
            var response = await http.PutAsync($"/api/projects/{slug}/dashboard/tiles/roadmap/output", body);
            Assert.True(response.StatusCode == HttpStatusCode.NoContent,
                $"Output write returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            var pending = Assert.Single((await http.GetFromJsonAsync<List<WorktreeMergeRequest>>($"/api/projects/{slug}/worktree-merges", json))!);
            Assert.Equal(WorktreeMergeStatus.Pending, pending.Status);
            Assert.Equal(WorktreeMergeJobKind.Maintenance, pending.JobKind);
            Assert.Equal("After dashboard integration", await File.ReadAllTextAsync(Path.Combine(pending.WorktreePath, ".dashboard", "roadmap", "output.md")));
            Assert.Equal("Before dashboard integration", await http.GetStringAsync($"/api/projects/{slug}/dashboard/tiles/roadmap/output"));
            Assert.Equal(originalHead, Git(repository, "rev-parse", "HEAD"));
            Assert.Empty(Git(repository, "status", "--porcelain"));

            var processed = await http.PostAsync($"/api/projects/{slug}/worktree-merges/process-next", null);
            processed.EnsureSuccessStatusCode();
            var integrated = (await processed.Content.ReadFromJsonAsync<WorktreeMergeRequest>(json))!;
            Assert.True(integrated.Status == WorktreeMergeStatus.Completed, integrated.Error);
            Assert.Equal(originalHead, Git(repository, "rev-parse", "HEAD"));
            Assert.Equal("Before dashboard integration", await File.ReadAllTextAsync(Path.Combine(tile, "output.md")));
            Assert.Equal("After dashboard integration", Git(repository, "show", $"{integrated.IntegratedCommit}:.dashboard/roadmap/output.md"));

            // Resuming the project lets the actual hosted processor synchronize the checkout.
            (await http.PostAsync($"/api/projects/{slug}/pause", null)).EnsureSuccessStatusCode();
            WorktreeMergeRequest? synchronized = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
            {
                while (synchronized?.SyncStatus != LocalCheckoutSyncStatus.Completed)
                {
                    synchronized = await http.GetFromJsonAsync<WorktreeMergeRequest>(
                        $"/api/projects/{slug}/worktree-merges/{pending.Id}", json, timeout.Token);
                    if (synchronized?.SyncStatus != LocalCheckoutSyncStatus.Completed)
                        await Task.Delay(100, timeout.Token);
                }
            }
            Assert.NotNull(synchronized);
            Assert.True(synchronized.SyncStatus == LocalCheckoutSyncStatus.Completed, synchronized.SyncError);
            Assert.Equal(integrated.IntegratedCommit, Git(repository, "rev-parse", "HEAD"));
            Assert.Equal("After dashboard integration", await http.GetStringAsync($"/api/projects/{slug}/dashboard/tiles/roadmap/output"));
            Assert.Empty(Git(repository, "status", "--porcelain"));
            await page.ReloadAsync();
            await Expect(content).ToContainTextAsync("After dashboard integration");
            Assert.Empty(errors);
            var artifact = Path.Combine(FindRepo(), ".artifacts", "dashboard-worktree-e2e.png");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            await page.ScreenshotAsync(new() { Path = artifact, FullPage = true });

            using var unchangedBody = new StringContent("After dashboard integration", Encoding.UTF8, "text/plain");
            var unchanged = await http.PutAsync($"/api/projects/{slug}/dashboard/tiles/roadmap/output", unchangedBody);
            Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
            var reused = Assert.Single((await http.GetFromJsonAsync<List<WorktreeMergeRequest>>($"/api/projects/{slug}/worktree-merges", json))!);
            Assert.Equal(pending.WorktreePath, reused.WorktreePath);
            Assert.Equal(WorktreeMergeStatus.Completed, reused.Status);
            Assert.Empty(Git(repository, "status", "--porcelain"));
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            // This test owns this unique synthetic directory and no user checkout.
            try { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* SQLite pools may still hold test files. */ }
        }
    }

    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim().TrimStart('\uFEFF');
    }

    private sealed class JourneyFactory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                // Keep the real integration worker; disable unrelated automation and external providers.
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType != typeof(WorktreeMergeQueueProcessor)
                    && (d.ImplementationFactory is not null || d.ImplementationType?.Assembly.GetName().Name?.StartsWith("KittyClaw", StringComparison.Ordinal) == true)).ToList())
                    services.Remove(descriptor);
            });
        }
    }
}

public sealed class DashboardWorktreeBrowserFactAttribute : FactAttribute
{
    public DashboardWorktreeBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_DASHBOARD_WORKTREE_E2E") != "1")
            Skip = "Set KITTYCLAW_DASHBOARD_WORKTREE_E2E=1 for the isolated dashboard browser journey.";
    }
}
