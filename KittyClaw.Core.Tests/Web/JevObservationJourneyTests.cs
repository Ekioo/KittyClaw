using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
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

/// <summary>Opt-in real-browser journey on an isolated ephemeral port; never contacts TypeSafe.</summary>
public sealed class JevObservationJourneyTests(ITestOutputHelper output)
{
    [DebugBrowserFact]
    public async Task On_demand_recommendation_and_automatic_routes_keep_history_in_comment_timeline()
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
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["KITTYCLAW_CLAUDE_BIN"] = mock,
            ["KITTYCLAW_GROK_BIN"] = mock,
            ["KITTYCLAW_CODEX_BIN"] = mock,
            ["KITTYCLAW_MISTRAL_BIN"] = mock,
        };
        var original = changes.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var pair in changes) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var factory = new DebugFactory(dataDir);
            factory.UseKestrel(0);
            using var http = factory.CreateClient();
            Assert.NotNull(http.BaseAddress);
            var baseUrl = http.BaseAddress.ToString().TrimEnd('/');
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
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.PageError += (_, error) => browserErrors.Add(error);
            page.Console += (_, message) => { output.WriteLine($"Browser {message.Type}: {message.Text}"); if (message.Text.Contains("WebSocket connected")) connected.TrySetResult(); };
            page.Response += (_, response) => { if (response.Status >= 400) output.WriteLine($"HTTP {response.Status}: {response.Url}"); };
            try
            {
            await page.GotoAsync($"{baseUrl}/settings");
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var settings = page.GetByTestId("jev-settings");
            await Expect(settings.GetByTestId("jev-global-status")).ToContainTextAsync("JEV unavailable");
            await Expect(settings.GetByTestId("jev-api-key")).ToBeEnabledAsync();
            await settings.GetByTestId("jev-api-key").FillAsync("debug-only-fake-key");
            await settings.GetByTestId("jev-api-key").PressAsync("Tab");
            await settings.GetByTestId("jev-save-key").ClickAsync();
            await Expect(settings.GetByTestId("jev-global-status")).ToContainTextAsync("JEV available");
            await Expect(settings.GetByTestId("jev-api-key")).ToHaveValueAsync("");
            Assert.Equal(0, factory.Handler.Count);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "00-jev-configured.png"), FullPage = true });
            await page.GotoAsync($"{baseUrl}/board/{slug}");
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
            await dialog.GetByTestId("jev-settings").Locator("summary").First.ClickAsync();
            var observation = dialog.GetByLabel("JEV qualification");
            await Expect(observation).Not.ToBeCheckedAsync();
            await observation.CheckAsync();
            await dialog.GetByTestId("jev-confidence-threshold").FillAsync("90");
            await dialog.Locator("label.workflow-check").Filter(new() { HasText = "Enabled" }).Locator("input").UncheckAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "01-observation-enabled.png"), FullPage = true });
            await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(dialog).Not.ToBeVisibleAsync();
            var processor = await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json);
            Assert.False(processor!.Enabled);
            Assert.True(processor.JevObservationOnly);
            await page.GotoAsync($"{baseUrl}/board/{slug}/ticket/{ticket!.Id}");
            var proposal = page.GetByTestId("jev-proposal");
            await Expect(page.GetByTestId("jev-ask")).ToHaveCountAsync(0);
            await Expect(page.GetByTestId("jev-assistance")).ToContainTextAsync("Confirm the ticket’s next step");
            await Expect(proposal).ToContainTextAsync("Suggested destination: Review", new() { Timeout = 60000 });
            await Expect(proposal.GetByTestId("jev-accept")).ToHaveTextAsync("Move to Review");
            await CaptureTicketAsync("02-direct-suggestion-and-action.png");
            await proposal.Locator("details > summary").First.ClickAsync();
            await Expect(proposal).ToContainTextAsync("Confidence: 90");
            await Expect(page.GetByTestId("ticket-worktree-error")).Not.ToBeVisibleAsync();
            await Expect(page.GetByTestId("jev-triage")).ToContainTextAsync("Problem");
            await Expect(page.GetByTestId("jev-triage")).ToContainTextAsync("Moderate");
            await Expect(page.GetByTestId("jev-triage")).ToContainTextAsync("15");
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
            await proposal.GetByTestId("jev-accept").ClickAsync();
            var history = page.GetByTestId("jev-history-entry");
            await Expect(history).ToContainTextAsync("Accepted");
            await Expect(page.GetByTestId("jev-assistance")).Not.ToContainTextAsync("Suggested destination: Review");
            await Expect(page.GetByTestId("jev-post-run-proposal")).Not.ToBeVisibleAsync();
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-history-entry")).ToContainTextAsync("Accepted");
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "03-jev-comment-history.png"), FullPage = true });

            // Routing is automatic without the legacy opt-in flag. Only the threshold changes.
            await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{source.Id}/processor",
                new SaveColumnProcessorRequest("JEV qualifier", "Qualify incoming tickets and propose review.",
                    Enabled: true, DefaultTargetColumnId: target.Id, JevObservationOnly: true, JevConfidenceThreshold: .9));
            var automaticResponse = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Automatically choose the next step", "owner", source.Name,
                    "Ready for review", PipelineId: source.PipelineId, ColumnId: source.Id));
            automaticResponse.EnsureSuccessStatusCode();
            var automaticTicket = (await automaticResponse.Content.ReadFromJsonAsync<Ticket>(json))!;
            await page.GotoAsync($"{baseUrl}/board/{slug}/ticket/{automaticTicket.Id}");
            await Expect(page.GetByTestId("jev-history-entry")).ToContainTextAsync("Automatically applied by JEV", new() { Timeout = 60000 });
            Assert.Equal(target.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{automaticTicket.Id}", json))!.ColumnId);
            await page.ScreenshotAsync(new() { Path = Path.Combine(dataDir, "04-automatic-route-comment.png"), FullPage = true });

            var classicTarget = columns!.Single(c => c.Name == "Todo");
            var done = columns!.Single(c => c.Name == "Done");
            var normalProcessor = await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{source.Id}/processor",
                new SaveColumnProcessorRequest("Implementation", "Complete the work and choose its next destination.",
                    DefaultTargetColumnId: classicTarget.Id, Routes: [new("review", target.Id)],
                    JevObservationOnly: false, JevConfidenceThreshold: .9));
            normalProcessor.EnsureSuccessStatusCode();
            var reviewer = await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{target.Id}/processor",
                new SaveColumnProcessorRequest("Reviewer", "Review the completed work.", DefaultTargetColumnId: done.Id));
            reviewer.EnsureSuccessStatusCode();
            var chainResponse = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Complete implementation and review", "owner", source.Name,
                    "Implement the change, review it, and finish the ticket.", PipelineId: source.PipelineId, ColumnId: source.Id));
            chainResponse.EnsureSuccessStatusCode();
            var chainTicket = (await chainResponse.Content.ReadFromJsonAsync<Ticket>(json))!;
            await page.GotoAsync($"{baseUrl}/board/{slug}/ticket/{chainTicket.Id}");
            await WaitForColumnAsync(chainTicket.Id, done.Id);
            await Expect(page.GetByTestId("jev-history-entry")).ToHaveCountAsync(2, new() { Timeout = 45000 });
            var chainExecutions = (await http.GetFromJsonAsync<List<ColumnExecution>>($"/api/projects/{slug}/column-executions?ticketId={chainTicket.Id}", json))!;
            Assert.Equal(2, chainExecutions.Count);
            Assert.All(chainExecutions, e => Assert.Equal(ColumnExecutionStatus.Completed, e.Status));
            Assert.Contains(chainExecutions, e => e.TargetColumnId == target.Id);
            Assert.Contains(chainExecutions, e => e.TargetColumnId == done.Id);
            Assert.Equal(2, factory.Dispatcher.Calls[chainTicket.Id]);
            var chainDecisions = await factory.Services.GetRequiredService<JevDecisionStore>().ListAsync(slug, chainTicket.Id);
            Assert.Equal(2, chainDecisions.Count);
            Assert.All(chainDecisions, d => Assert.True(d.Automatic));
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-history-entry")).ToHaveCountAsync(2);
            await CaptureTicketAsync("05-complete-agent-review-done.png");

            // Change the simulated judgment, keeping the configured workflow stable.
            // Workflow edits racing an active sweep are deliberately rejected by the engine.
            factory.Handler.Confidence = .5;
            var fallbackResponse = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Classic routing when JEV is uncertain", "owner", source.Name,
                    "Use the normal route when JEV is below the configured confidence threshold.", PipelineId: source.PipelineId, ColumnId: source.Id));
            fallbackResponse.EnsureSuccessStatusCode();
            var fallbackTicket = (await fallbackResponse.Content.ReadFromJsonAsync<Ticket>(json))!;
            await page.GotoAsync($"{baseUrl}/board/{slug}/ticket/{fallbackTicket.Id}");
            await WaitForColumnAsync(fallbackTicket.Id, classicTarget.Id);
            await page.ReloadAsync();
            await Expect(page.Locator(".timeline-event").Filter(new() { HasText = "Continuing with normal processing and classic routing" })).ToBeVisibleAsync();
            var fallbackExecution = Assert.Single((await http.GetFromJsonAsync<List<ColumnExecution>>($"/api/projects/{slug}/column-executions?ticketId={fallbackTicket.Id}", json))!);
            Assert.Equal(ColumnExecutionStatus.Completed, fallbackExecution.Status);
            Assert.True(fallbackExecution.JevClassicFallback);
            Assert.Equal(classicTarget.Id, fallbackExecution.TargetColumnId);
            Assert.Empty(await factory.Services.GetRequiredService<JevDecisionStore>().ListAsync(slug, fallbackTicket.Id));
            await Expect(page.Locator("[data-testid=jev-accept]:disabled")).ToHaveCountAsync(0);
            await CaptureTicketAsync("06-uncertain-jev-classic-route.png");
            await Task.Delay(TimeSpan.FromSeconds(16));
            Assert.Equal(1, factory.Dispatcher.Calls[fallbackTicket.Id]);
            Assert.Equal(2, factory.Dispatcher.Calls[chainTicket.Id]);
            Assert.Equal(classicTarget.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{fallbackTicket.Id}", json))!.ColumnId);
            Assert.Empty(browserErrors);
            output.WriteLine($"PASS: {baseUrl}/board/{slug}/ticket/{ticket.Id}");
            output.WriteLine($"Data and screenshots retained at {dataDir}");
            output.WriteLine($"PASS: manual recommendation and persistent comment history; automatic qualification; agent → JEV Review → agent → JEV Done; uncertain JEV → classic Todo. No repeated dispatches or browser errors.");

            async Task WaitForColumnAsync(int id, int expectedColumn)
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    if ((await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{id}", json))?.ColumnId == expectedColumn) return;
                    await Task.Delay(500);
                }
                Assert.Fail($"Ticket #{id} did not reach column #{expectedColumn}.");
            }

            async Task CaptureTicketAsync(string name)
            {
                // Blazor replaces the prerendered panel when the circuit reconnects after reload.
                // Acquire a fresh element if that replacement interrupts screenshot capture.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await page.Locator(".ticket-panel").ScreenshotAsync(new() { Path = Path.Combine(dataDir, name) });
                        return;
                    }
                    catch (PlaywrightException ex) when (attempt < 4 && ex.Message.Contains("not attached to the DOM", StringComparison.Ordinal))
                    {
                        await Task.Delay(250);
                    }
                }
            }
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

    private sealed class DebugFactory(string dataDir) : WebApplicationFactory<CreateProjectRequest>
    {
        public FixtureHandler Handler { get; } = new();
        public FixtureDispatcher Dispatcher { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IColumnAgentDispatcher>();
                services.AddSingleton<IColumnAgentDispatcher>(Dispatcher);
                services.RemoveAll<JevConfigurationService>();
                services.AddSingleton(sp => new JevConfigurationService(sp.GetRequiredService<ProjectSecretVault>(), dataDir, () => null));
                services.RemoveAll<JevQualificationService>();
                services.AddSingleton(sp => new JevQualificationService(sp.GetRequiredService<ProjectService>(),
                    sp.GetRequiredService<ProjectSecretVault>(), new HttpClient(Handler), sp.GetRequiredService<JevConfigurationService>()));
            });
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Count;
        public double Confidence = .9;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.ToString());
            Assert.Equal("debug-only-fake-key", request.Headers.Authorization!.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var options = body.RootElement.GetProperty("questions").GetProperty("routing").GetProperty("criteria");
            var candidates = options.EnumerateObject().Where(p => p.Name != "none").ToList();
            var selected = candidates.OrderByDescending(p => p.Value.GetString()!.StartsWith("Review:")).First().Name;
            var answers = JsonSerializer.SerializeToNode(JevResponseFixture.Answers(selected))!;
            answers["routing"]!["confidence"] = Confidence;
            answers["routing"]!["probabilities"] = JsonSerializer.SerializeToNode(options.EnumerateObject().ToDictionary(p => p.Name,
                p => p.Name == selected ? .95 : .05 / (options.EnumerateObject().Count() - 1)));
            Interlocked.Increment(ref Count);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    model = "jev-debug-fixture", answers,
                    usage = new { input_tokens = 450, output_tokens = 70 }
                })
            };
        }
    }

    private sealed class FixtureDispatcher : IColumnAgentDispatcher
    {
        public System.Collections.Concurrent.ConcurrentDictionary<int, int> Calls { get; } = new();
        public Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Calls.AddOrUpdate(ticket.Id, 1, (_, count) => count + 1);
            return Task.FromResult(new ColumnDispatchResult(new AgentRun { RunId = execution.Id, ProjectSlug = slug, TicketId = ticket.Id,
                AgentName = processor.Name, SkillFile = "", ConcurrencyGroup = "debug-fixture", StartedAt = DateTime.UtcNow },
                new ColumnAgentResult("completed", processor.RequiredSkills, "Completed by the isolated fixture", Evidence: new(ticket.UpdatedAt)), null));
        }
    }
}

public sealed class DebugBrowserFactAttribute : FactAttribute
{
    public DebugBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_JEV_DEBUG_E2E") != "1")
            Skip = "Opt-in debug browser journey: set KITTYCLAW_JEV_DEBUG_E2E=1. Requires Playwright Chromium.";
    }
}

