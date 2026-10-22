using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

/// <summary>Opt-in real-browser journey against the isolated debug port; never contacts TypeSafe.</summary>
public sealed class JevObservationJourneyTests(ITestOutputHelper output)
{
    [DebugBrowserFact]
    public async Task Activate_observation_in_column_and_show_proposal_without_moving_ticket()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var repo = FindRepo();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KittyClaw-debug", "jev-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        var mock = Path.Combine(repo, "KittyClaw.ClaudeMock", "bin", "Debug", "net10.0", "claude.exe");
        Assert.True(File.Exists(mock), "Build KittyClaw.ClaudeMock before this journey.");
        var changes = new Dictionary<string, string?>
        {
            ["KITTYCLAW_DATA_DIR"] = dataDir,
            ["ASPNETCORE_URLS"] = "http://localhost:5232",
            ["KITTYCLAW_CLAUDE_BIN"] = mock,
            ["KITTYCLAW_GROK_BIN"] = mock,
            ["KITTYCLAW_CODEX_BIN"] = mock,
            ["KITTYCLAW_MISTRAL_BIN"] = mock,
        };
        var original = changes.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var pair in changes) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var factory = new DebugFactory();
            factory.UseKestrel(5232);
            using var http = factory.CreateClient();
            Assert.Equal(5232, http.BaseAddress!.Port);
            var created = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("JEV observation journey"));
            created.EnsureSuccessStatusCode();
            var slug = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
            var workspace = Path.Combine(dataDir, "workspace");
            Directory.CreateDirectory(workspace);
            await RunGitAsync(workspace, "init", "--initial-branch=main");
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "# Isolated JEV browser fixture\n");
            await RunGitAsync(workspace, "add", "README.md");
            await RunGitAsync(workspace, "-c", "user.name=JEV QA", "-c", "user.email=jev-qa@kittyclaw.local", "commit", "-m", "Initialize isolated browser fixture");
            var configured = await http.PatchAsJsonAsync($"/api/projects/{slug}",
                new UpdateProjectRequest(WorkspacePath: workspace, RepositoryPath: workspace, WorktreesEnabled: false));
            configured.EnsureSuccessStatusCode();
            var columns = await http.GetFromJsonAsync<List<BoardColumn>>($"/api/projects/{slug}/columns", json);
            var source = columns!.First(c => c.Name == "Backlog");
            var target = columns!.First(c => c.Name == "Review");
            var saved = await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{source.Id}/processor",
                new SaveColumnProcessorRequest("JEV qualifier", "Qualify incoming tickets and propose review.",
                    Enabled: false, DefaultTargetColumnId: target.Id));
            saved.EnsureSuccessStatusCode();
            var secret = await http.PutAsJsonAsync($"/api/projects/{slug}/secrets/TYPESAFE_API_KEY", new { value = "debug-only-fake-key" });
            secret.EnsureSuccessStatusCode();
            var newTicket = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Review the JEV routing proposal", "owner", source.Name,
                    "This ticket is ready for review. It must remain in Backlog during observation.",
                    PipelineId: source.PipelineId, ColumnId: source.Id));
            newTicket.EnsureSuccessStatusCode();
            var ticket = await newTicket.Content.ReadFromJsonAsync<Ticket>(json);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 1100 }, Locale = "en-US", TimezoneId = "Europe/Paris"
            });
            var page = await context.NewPageAsync();
            var browserErrors = new List<string>();
            page.PageError += (_, error) => browserErrors.Add(error);
            page.Console += (_, message) => output.WriteLine($"Browser {message.Type}: {message.Text}");
            page.Response += (_, response) => { if (response.Status >= 400) output.WriteLine($"HTTP {response.Status}: {response.Url}"); };
            try
            {
            await page.GotoAsync($"http://localhost:5232/board/{slug}");
            var sourceHeader = page.Locator(".column-header").Filter(new() { HasText = "Backlog" });
            // The prerendered header can be visible before Blazor has connected its circuit.
            for (var attempt = 0; attempt < 20 && !await page.Locator(".column-context-menu-configure").IsVisibleAsync(); attempt++)
            {
                await sourceHeader.ClickAsync(new() { Button = MouseButton.Right });
                await Task.Delay(500);
            }
            await page.Locator(".column-context-menu-configure").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Expect(dialog).ToBeVisibleAsync();
            await dialog.Locator(".column-editor-tabs button").Nth(1).ClickAsync();
            var observation = dialog.GetByLabel("JEV qualification — observation only");
            await Expect(observation).Not.ToBeCheckedAsync();
            await observation.CheckAsync();
            await dialog.Locator("label.workflow-check").Filter(new() { HasText = "Enabled" }).Locator("input").CheckAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "01-observation-enabled.png"), FullPage = true });
            await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(dialog).Not.ToBeVisibleAsync();
            var processor = await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json);
            Assert.True(processor!.Enabled);
            Assert.True(processor.JevObservationOnly);
            await page.GotoAsync($"http://localhost:5232/board/{slug}/ticket/{ticket!.Id}");
            var proposal = page.GetByTestId("jev-proposal");
            await Expect(proposal).ToContainTextAsync("Suggested destination: Review", new() { Timeout = 60000 });
            await Expect(proposal).ToContainTextAsync("Confidence: 90");
            await Expect(page.GetByTestId("ticket-worktree-error")).Not.ToBeVisibleAsync();
            await proposal.Locator("summary").ClickAsync();
            await Expect(proposal).ToContainTextAsync("95");
            await Expect(proposal).ToContainTextAsync("jev-debug-fixture");
            await proposal.ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "02-ticket-proposal.png"), FullPage = true });
            // Observe for more than one background sweep; unchanged inputs must not cause repeats or movement.
            var until = DateTime.UtcNow.AddSeconds(32);
            while (DateTime.UtcNow < until)
            {
                var actual = await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{ticket.Id}", json);
                Assert.Equal(source.Id, actual!.ColumnId);
                Assert.Equal(ticket.UpdatedAt, actual.UpdatedAt);
                await Task.Delay(1000);
            }
            var executions = await http.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/column-executions");
            Assert.Equal(0, executions.GetArrayLength());
            Assert.Equal(1, factory.Handler.Count);
            Assert.Empty(browserErrors);
            output.WriteLine($"PASS: http://localhost:5232/board/{slug}/ticket/{ticket.Id}");
            output.WriteLine($"Data and screenshots retained at {dataDir}");
            output.WriteLine($"Ticket remained in {source.Name} (#{source.Id}); proposed {target.Name} (#{target.Id}); one mock API call; zero executions.");
            }
            catch
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "failure.png"), FullPage = true });
                output.WriteLine($"Failure screenshot: {dataDir}");
                output.WriteLine(await page.Locator("body").InnerTextAsync());
                output.WriteLine(string.Join("\n", browserErrors));
                throw;
            }
        }
        finally
        {
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static async Task RunGitAsync(string directory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        await stdout;
    }

    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }

    private sealed class DebugFactory : WebApplicationFactory<CreateProjectRequest>
    {
        public FixtureHandler Handler { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<JevQualificationService>();
                services.AddSingleton(sp => new JevQualificationService(sp.GetRequiredService<ProjectService>(),
                    sp.GetRequiredService<ProjectSecretVault>(), new HttpClient(Handler)));
            });
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.ToString());
            Assert.Equal("debug-only-fake-key", request.Headers.Authorization!.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var options = body.RootElement.GetProperty("questions").GetProperty("routing").GetProperty("criteria");
            var selected = options.EnumerateObject().Single(p => p.Name != "none").Name;
            Interlocked.Increment(ref Count);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    model = "jev-debug-fixture", answers = new { routing = new { type = "choice", choice = selected,
                        confidence = 0.9, probabilities = new Dictionary<string, double> { [selected] = 0.95, ["none"] = 0.05 } } }
                })
            };
        }
    }
}

public sealed class DebugBrowserFactAttribute : FactAttribute
{
    public DebugBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_JEV_DEBUG_E2E") != "1")
            Skip = "Opt-in debug browser journey: set KITTYCLAW_JEV_DEBUG_E2E=1. Requires free port 5232 and Playwright Chromium.";
    }
}

