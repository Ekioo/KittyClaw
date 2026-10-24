using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Services;
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

public sealed class JevWorkReviewJourneyTests(ITestOutputHelper output)
{
    [DebugBrowserFact]
    public async Task Owner_approves_execution_profiles_and_acceptance_reviews_with_evidence_and_correction_feedback()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KittyClaw-debug", "jev-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        var originalData = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        var originalUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", data);
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        try
        {
            await using var factory = new Factory(); factory.UseKestrel(0);
            using var http = factory.CreateClient();
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            var response = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("JEV work reviews")); response.EnsureSuccessStatusCode();
            var slug = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
            var workspace = Path.Combine(data, "workspace"); Directory.CreateDirectory(workspace);
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("init");
            using (var git = System.Diagnostics.Process.Start(start)!)
            {
                var stdout = git.StandardOutput.ReadToEndAsync(); var stderr = git.StandardError.ReadToEndAsync();
                await git.WaitForExitAsync(); Assert.True(git.ExitCode == 0, await stderr); await stdout;
            }
            (await http.PatchAsJsonAsync($"/api/projects/{slug}", new UpdateProjectRequest(WorkspacePath: workspace, RepositoryPath: workspace, WorktreesEnabled: false))).EnsureSuccessStatusCode();
            (await http.PutAsJsonAsync($"/api/projects/{slug}/secrets/TYPESAFE_API_KEY", new { value = "debug-only-key" })).EnsureSuccessStatusCode();
            var columns = (await http.GetFromJsonAsync<List<BoardColumn>>($"/api/projects/{slug}/columns", json))!;
            var source = columns.Single(c => c.Name == "Backlog"); var review = columns.Single(c => c.Name == "Review");
            var done = columns.Single(c => c.Name == "Done"); var todo = columns.Single(c => c.Name == "Todo");
            var selectedModel = KittyClaw.Core.Models.ClaudeModelCatalog.Models.Last();
            var defaultModel = KittyClaw.Core.Models.ClaudeModelCatalog.DefaultModel;
            var skills = factory.Services.GetRequiredService<ProjectSkillService>();
            var required = await skills.CreateAsync(slug, "Mandatory checks", "Always run required checks.");
            var testing = await skills.CreateAsync(slug, "Testing", "Verify ticket behavior.");
            var other = await skills.CreateAsync(slug, "Documentation", "Document changes.");
            (await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{source.Id}/processor",
                new SaveColumnProcessorRequest("Worker", "Implement work", Model: defaultModel, Enabled: false,
                    AvailableSkills: [testing.Slug, other.Slug], RequiredSkills: [required.Slug], DefaultTargetColumnId: review.Id))).EnsureSuccessStatusCode();
            (await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{review.Id}/processor",
                new SaveColumnProcessorRequest("Acceptance", "Review acceptance criteria", Enabled: false, DefaultTargetColumnId: done.Id,
                    Routes: [new("changes_requested", todo.Id)]))).EnsureSuccessStatusCode();
            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1100 } });
            var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
            async Task<ILocator> OpenProcessorAsync(string name)
            {
                await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
                for (var attempt = 0; attempt < 20 && !await page.Locator(".column-context-menu-configure").IsVisibleAsync(); attempt++)
                {
                    await page.Locator(".column-header").Filter(new() { HasText = name }).ClickAsync(new() { Button = MouseButton.Right });
                    await Task.Delay(500);
                }
                await page.Locator(".column-context-menu-configure").ClickAsync();
                var dialog = page.GetByRole(AriaRole.Dialog);
                await dialog.Locator(".column-editor-tabs button").Nth(1).ClickAsync(); return dialog;
            }
            async Task SaveProcessorAsync(ILocator dialog)
            {
                await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync(); await Expect(dialog).Not.ToBeVisibleAsync();
            }
            var dialog = await OpenProcessorAsync("Review");
            await Expect(dialog.GetByTestId("column-mode-Review")).Not.ToBeCheckedAsync();
            await dialog.GetByTestId("column-mode-Review").CheckAsync();
            await dialog.GetByTestId("jev-column-criteria").FillAsync("Tests pass\nLogin works");
            await SaveProcessorAsync(dialog);
            dialog = await OpenProcessorAsync("Backlog");
            await dialog.GetByTestId("column-mode-Agent").CheckAsync();
            await Expect(dialog.GetByTestId("jev-profile-mode")).Not.ToBeCheckedAsync();
            await dialog.GetByTestId("jev-profile-mode").CheckAsync();
            await dialog.GetByTestId("jev-add-profile").ClickAsync();
            var editor = dialog.GetByTestId("jev-profile-editor");
            await editor.Locator("input.settings-input").Nth(0).FillAsync("Fast verification");
            await editor.Locator("input.settings-input").Nth(1).FillAsync("Small changes requiring tests");
            await editor.Locator("select").SelectOptionAsync(selectedModel);
            await editor.GetByLabel("Testing", new() { Exact = true }).CheckAsync();
            await SaveProcessorAsync(dialog);
            var processor = (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json))!;
            Assert.True(processor.JevExecutionSelection); Assert.Equal("Fast verification", Assert.Single(processor.JevExecutionProfiles).Name);
            var acceptance = (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{review.Id}/processor", json))!;
            Assert.True(acceptance.JevAcceptanceValidation); Assert.Equal(2, acceptance.JevAcceptanceCriteria.Count); Assert.False(acceptance.JevAutomaticRouting);
            async Task<Ticket> CreateTicketAsync(string title)
            {
                var created = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets", new CreateTicketRequest(title, "owner", source.Name,
                    "Implement and verify login behavior.", ColumnId: source.Id, PipelineId: source.PipelineId));
                created.EnsureSuccessStatusCode(); return (await created.Content.ReadFromJsonAsync<Ticket>(json))!;
            }
            factory.Handler.Status = "unmet";
            var ticket = await CreateTicketAsync("Review missing criteria");
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{ticket.Id}").ToString());
            var panel = page.GetByTestId("jev-work-review");
            await Expect(panel.GetByTestId("jev-selected-profile")).ToContainTextAsync("Fast verification", new() { Timeout = 45000 });
            await Expect(panel).ToContainTextAsync(selectedModel);
            await Task.Delay(20000); Assert.Empty(factory.Dispatcher.Models);
            await panel.GetByTestId("jev-work-approve").ClickAsync();
            await Expect(panel.GetByTestId("jev-criteria-results")).ToContainTextAsync("Needs work", new() { Timeout = 45000 });
            Assert.Equal(selectedModel, Assert.Single(factory.Dispatcher.Models));
            Assert.Contains(required.Slug, Assert.Single(factory.Dispatcher.Skills));
            Assert.Contains(testing.Slug, factory.Dispatcher.Skills.Single()); Assert.DoesNotContain(other.Slug, factory.Dispatcher.Skills.Single());
            Assert.Equal(defaultModel, (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json))!.Model);
            await Expect(panel.GetByTestId("jev-missing-criteria")).ToContainTextAsync("Needs review: 2");
            await Expect(page.GetByTestId("ticket-current-column")).ToContainTextAsync("Review", new() { Timeout = 20000 });
            await Expect(panel.Locator(".jev-technical")).Not.ToHaveAttributeAsync("open", "");
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "criteria-summary.png"), FullPage = true });
            await panel.GetByTestId("jev-review-evidence").Locator("summary").First.ClickAsync();
            await Expect(panel.GetByTestId("jev-review-evidence")).ToContainTextAsync("Verified");
            await Task.Delay(20000);
            Assert.Equal(review.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{ticket.Id}", json))!.ColumnId);
            Assert.Single(factory.Dispatcher.Models);
            await panel.GetByTestId("jev-work-approve").ClickAsync();
            await Expect(panel).ToContainTextAsync("Why approve despite these points?");
            await Expect(panel.GetByTestId("jev-work-confirm")).ToBeDisabledAsync();
            await panel.GetByTestId("jev-work-cancel").ClickAsync();
            await panel.GetByTestId("jev-work-reject").ClickAsync();
            await panel.GetByTestId("jev-work-reason").FillAsync("Repair the missing behavior and provide fresh verification.");
            await panel.EvaluateAsync("element => element.scrollIntoView({block:'center'})");
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "criteria-evidence-and-missing.png"), FullPage = true });
            await panel.GetByTestId("jev-work-confirm").ClickAsync();
            await Expect(page.GetByTestId("jev-work-history").Filter(new() { HasText = "Rejected" })).ToBeVisibleAsync();
            var corrected = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{ticket.Id}", json))!;
            Assert.Equal(todo.Id, corrected.ColumnId); Assert.Contains(corrected.Comments, c => c.Content.Contains("Login works") && c.Content.Contains("Tests pass"));

            factory.Handler.Status = "satisfied";
            var second = await CreateTicketAsync("Approve verified criteria");
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{second.Id}").ToString());
            await Expect(panel.GetByTestId("jev-selected-profile")).ToContainTextAsync("Fast verification", new() { Timeout = 45000 });
            await panel.GetByTestId("jev-work-approve").ClickAsync();
            await Expect(panel.GetByTestId("jev-missing-criteria")).ToContainTextAsync("Ready for your approval", new() { Timeout = 45000 });
            await panel.GetByTestId("jev-work-approve").ClickAsync();
            await Expect(page.GetByTestId("jev-work-history").Filter(new() { HasText = "Work review" })).ToContainTextAsync("Approved by owner");
            var completionUntil = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < completionUntil && (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{second.Id}", json))!.ColumnId != done.Id)
                await Task.Delay(100);
            Assert.Equal(done.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{second.Id}", json))!.ColumnId);
            await page.ReloadAsync(); await Expect(page.GetByTestId("jev-work-history").Filter(new() { HasText = "Work review" })).ToContainTextAsync("Tests pass");
            await Expect(page.GetByTestId("jev-work-history").Filter(new() { HasText = "Work review" })).ToContainTextAsync("Verified");
            Assert.Equal(2, factory.Dispatcher.Models.Count); Assert.Empty(errors);
            var costs = factory.Services.GetRequiredService<CostReportService>();
            var project = (await factory.Services.GetRequiredService<ProjectService>().GetProjectAsync(slug))!;
            factory.Services.GetRequiredService<KittyClaw.Core.Automation.CostTracker>().LogRun(
                factory.Services.GetRequiredService<ProjectService>().ResolveWorkspacePath(project),
                new(DateTime.UtcNow, "Cost fixture", second.Id, "other-agent-model", 10, 10, 0, 0, 1m, 1, 0,
                    ProjectSlug: slug, PipelineId: second.PipelineId, RunId: "cost-fixture-agent"));
            await costs.RefreshAsync();
            var day = DateOnly.FromDateTime(DateTime.Now);
            var costReport = await costs.GetReportAsync(new(day, day));
            var jev = Assert.Single(costReport.Projects).Jev!;
            Assert.Equal(factory.Handler.Count, jev.Calls);
            Assert.Equal(4, jev.Calls);
            Assert.Equal(1.000168m, costReport.TotalUsd);
            await page.GotoAsync(new Uri(http.BaseAddress!, "/costs").ToString());
            await Expect(page.GetByTestId("jev-cost-summary")).ToContainTextAsync("JEV · 4 calls");
            await Expect(page.GetByTestId("jev-cost-summary")).ToContainTextAsync("$0.000168");
            await Expect(page.GetByTestId("cost-visible-total")).ToContainTextAsync("$1.00");
            // The costs page can be prerendered before its new Blazor circuit is interactive.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await page.Locator(".cost-model").SelectOptionAsync("jev-1.14.0");
                if ((await page.GetByTestId("cost-visible-total").InnerTextAsync()).Contains("$0.000168")) break;
                await Task.Delay(250);
            }
            await Expect(page.GetByTestId("cost-visible-total")).ToContainTextAsync("$0.000168");
            await Expect(page.Locator(".cost-model")).ToHaveValueAsync("jev-1.14.0");
            await Expect(page.GetByTestId("jev-cost-summary")).ToContainTextAsync("4 calls");
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "jev-costs.png"), FullPage = true });
            Assert.Empty(errors);
            output.WriteLine($"PASS: manually approved model/skills used per ticket, criteria held for owner review, missing criteria and verified evidence shown, corrections routed with feedback, accepted ticket reached Done. Debug data: {data}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", originalData);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalUrls);
        }
    }

    private sealed class Factory : WebApplicationFactory<CreateProjectRequest>
    {
        public JevWorkReviewTests.Handler Handler { get; } = new();
        public Dispatcher Dispatcher { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IColumnAgentDispatcher>(); services.AddSingleton<IColumnAgentDispatcher>(sp => { Dispatcher.DataDir = sp.GetRequiredService<ProjectService>().DataDir; Dispatcher.ChecksFail = () => Handler.Status == "unmet"; return Dispatcher; });
                services.RemoveAll<JevConfigurationService>();
                services.AddSingleton(sp => new JevConfigurationService(sp.GetRequiredService<ProjectSecretVault>(), sp.GetRequiredService<ProjectService>().DataDir, environmentKey: () => null));
                services.RemoveAll<JevWorkReviewService>();
                services.AddSingleton(sp => new JevWorkReviewService(sp.GetRequiredService<ProjectService>(), sp.GetRequiredService<JevConfigurationService>(), new HttpClient(Handler), sp.GetRequiredService<JevCostRecorder>()));
            });
        }
    }
    private sealed class Dispatcher : IColumnAgentDispatcher
    {
        public string DataDir = "";
        public Func<bool> ChecksFail = () => false;
        public List<string?> Models = [];
        public List<List<string>> Skills = [];
        public Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Models.Add(processor.Model); Skills.Add(processor.AvailableSkills);
            var at = DateTime.UtcNow;
            var checksFailed = ChecksFail();
            new EvidenceStore(DataDir).SaveRun(new() { ProjectSlug = slug, TicketId = ticket.Id.ToString(), CapturedAt = at,
                RunIds = [execution.Id], Status = EvidenceStatus.Complete,
                CommandsRun = [new("fixture checks", null, checksFailed ? 1 : 0, !checksFailed, null, checksFailed ? 0 : 4, checksFailed ? 1 : 0, new("process-exit", "unknown", execution.Id, at, EvidenceTrust.Verified))] });
            return Task.FromResult(new ColumnDispatchResult(new AgentRun { RunId = execution.Id, ProjectSlug = slug, TicketId = ticket.Id,
                AgentName = processor.Name, SkillFile = "fixture", ConcurrencyGroup = "fixture", StartedAt = at, Status = AgentRunStatus.Completed },
                new("completed", processor.RequiredSkills, "Verification captured."), null));
        }
    }
}
