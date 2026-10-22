using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Evidence;
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

public sealed class JevLiveWorkReviewJourneyTests(ITestOutputHelper output)
{
    [LiveJevBrowserFact]
    public async Task Owner_approves_execution_profiles_and_acceptance_reviews_with_evidence_and_correction_feedback()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KittyClaw-debug", "jev-live-work-" + Guid.NewGuid().ToString("N"));
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
            var availability = await factory.Services.GetRequiredService<JevConfigurationService>().GetAsync(slug);
            Assert.True(availability.Available, "A local TypeSafe key must be available.");
            Assert.Equal("environment", availability.Source);
            Assert.False(availability.ConfiguredInKittyClaw);
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
                await dialog.Locator("label.workflow-check").Filter(new() { HasText = "Enabled" }).Locator("input").CheckAsync();
                await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync(); await Expect(dialog).Not.ToBeVisibleAsync();
            }
            var dialog = await OpenProcessorAsync("Review");
            await dialog.GetByTestId("jev-settings").Locator("summary").First.ClickAsync();
            await Expect(dialog.GetByTestId("jev-acceptance-mode")).Not.ToBeCheckedAsync();
            await dialog.GetByTestId("jev-acceptance-mode").CheckAsync();
            await dialog.GetByTestId("jev-column-criteria").FillAsync("The verification process exits successfully\nAll four verification tests pass with zero failures");
            await SaveProcessorAsync(dialog);
            dialog = await OpenProcessorAsync("Backlog");
            await dialog.GetByTestId("jev-settings").Locator("summary").First.ClickAsync();
            await Expect(dialog.GetByTestId("jev-profile-mode")).Not.ToBeCheckedAsync();
            await dialog.GetByTestId("jev-profile-mode").CheckAsync();
            await dialog.GetByTestId("jev-add-profile").ClickAsync();
            var editor = dialog.GetByTestId("jev-profile-editor").First;
            await editor.Locator("input.settings-input").Nth(0).FillAsync("Fast verification");
            await editor.Locator("input.settings-input").Nth(1).FillAsync("Small changes requiring tests");
            await editor.Locator("select").SelectOptionAsync(selectedModel);
            await editor.GetByLabel("Testing", new() { Exact = true }).CheckAsync();
            await dialog.GetByTestId("jev-add-profile").ClickAsync();
            var documentationProfile = dialog.GetByTestId("jev-profile-editor").Last;
            await documentationProfile.Locator("input.settings-input").Nth(0).FillAsync("Documentation only");
            await documentationProfile.Locator("input.settings-input").Nth(1).FillAsync("Use only for documentation editing with no executable code changes or tests.");
            await documentationProfile.Locator("select").SelectOptionAsync(defaultModel);
            await documentationProfile.GetByLabel("Documentation", new() { Exact = true }).CheckAsync();
            await SaveProcessorAsync(dialog);
            var processor = (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json))!;
            Assert.True(processor.JevExecutionSelection); Assert.Equal(2, processor.JevExecutionProfiles.Count);
            var acceptance = (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{review.Id}/processor", json))!;
            Assert.True(acceptance.JevAcceptanceValidation); Assert.Equal(2, acceptance.JevAcceptanceCriteria.Count); Assert.False(acceptance.JevAutomaticRouting);
            async Task<Ticket> CreateTicketAsync(string title)
            {
                var created = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets", new CreateTicketRequest(title, "owner", source.Name,
                    "Make a small executable code change and run exactly four verification tests. This is a coding and testing task, not a documentation edit.", ColumnId: source.Id, PipelineId: source.PipelineId));
                created.EnsureSuccessStatusCode(); return (await created.Content.ReadFromJsonAsync<Ticket>(json))!;
            }
            factory.Dispatcher.ChecksFail = true;
            var ticket = await CreateTicketAsync("Review missing criteria");
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{ticket.Id}").ToString());
            var panel = page.GetByTestId("jev-work-review");
            await Expect(panel.GetByTestId("jev-selected-profile")).ToContainTextAsync("Fast verification", new() { Timeout = 45000 });
            await Expect(panel).ToContainTextAsync(selectedModel);
            var reviews = factory.Services.GetRequiredService<JevWorkReviewService>();
            var selected = (await reviews.GetAsync(slug, ticket.Id, source.Id))!;
            Assert.StartsWith("jev-", selected.Model);
            output.WriteLine($"Live profile: model={selected.Model}, profile={selected.Profile?.Name}, confidence={selected.Confidence:P1}");
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "selected-model-and-skills.png"), FullPage = true });
            await Task.Delay(20000); Assert.Empty(factory.Dispatcher.Models);
            await panel.GetByTestId("jev-work-approve").ClickAsync();
            await Expect(panel.GetByTestId("jev-criteria-results")).ToContainTextAsync("Needs work", new() { Timeout = 45000 });
            var rejectedReview = (await reviews.GetAsync(slug, ticket.Id, review.Id))!;
            Assert.StartsWith("jev-", rejectedReview.Model);
            Assert.All(rejectedReview.Criteria, c => Assert.NotNull(c.EvidenceId));
            output.WriteLine($"Live failed review: model={rejectedReview.Model}; " + string.Join("; ", rejectedReview.Criteria.Select(c => $"{c.Criterion}={c.Status}, proof={c.EvidenceId}, confidence={c.Confidence:P1}")));
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
            await Expect(page.GetByTestId("jev-work-history")).ToContainTextAsync("Rejected");
            var corrected = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{ticket.Id}", json))!;
            Assert.Equal(todo.Id, corrected.ColumnId); Assert.Contains(corrected.Comments, c => c.Content.Contains("All four verification tests") && c.Content.Contains("verification process"));

            factory.Dispatcher.ChecksFail = false;
            var second = await CreateTicketAsync("Approve verified criteria");
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{second.Id}").ToString());
            await Expect(panel.GetByTestId("jev-selected-profile")).ToContainTextAsync("Fast verification", new() { Timeout = 45000 });
            await panel.GetByTestId("jev-work-approve").ClickAsync();
            await Expect(panel.GetByTestId("jev-missing-criteria")).ToContainTextAsync("Ready for your approval", new() { Timeout = 45000 });
            await panel.GetByTestId("jev-work-approve").ClickAsync();
            await Expect(page.GetByTestId("jev-work-history")).ToContainTextAsync("Approved by owner");
            var completionUntil = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < completionUntil && (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{second.Id}", json))!.ColumnId != done.Id)
                await Task.Delay(100);
            Assert.Equal(done.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{second.Id}", json))!.ColumnId);
            await page.ReloadAsync(); await Expect(page.GetByTestId("jev-work-history")).ToContainTextAsync("All four verification tests");
            await Expect(page.GetByTestId("jev-work-history")).ToContainTextAsync("Verified");
            Assert.Equal(2, factory.Dispatcher.Models.Count); Assert.Empty(errors);
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
        public Dispatcher Dispatcher { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IColumnAgentDispatcher>();
                services.AddSingleton<IColumnAgentDispatcher>(sp => { Dispatcher.DataDir = sp.GetRequiredService<ProjectService>().DataDir; return Dispatcher; });
            });
        }
    }
    private sealed class Dispatcher : IColumnAgentDispatcher
    {
        public string DataDir = "";
        public bool ChecksFail;
        public List<string?> Models = [];
        public List<List<string>> Skills = [];
        public async Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Models.Add(processor.Model); Skills.Add(processor.AvailableSkills);
            // Execute a real bounded verification process; the first ticket has one intentional failing assertion.
            var script = "import unittest\nclass Checks(unittest.TestCase):\n" +
                string.Join("\n", Enumerable.Range(0, 4).Select(i => $" def test_{i}(self): self.assertEqual({(ChecksFail && i == 0 ? 0 : 1)}, 1)")) +
                "\nunittest.main()\n";
            var start = new System.Diagnostics.ProcessStartInfo("python")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(script);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct); var verificationOutput = await stdout + await stderr;
            Assert.Contains("Ran 4 tests", verificationOutput);
            var checksFailed = process.ExitCode != 0;
            Assert.Equal(ChecksFail, checksFailed);
            var runDirectory = Path.Combine(DataDir, "runs", execution.Id);
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "verification.txt"), verificationOutput, ct);
            var at = DateTime.UtcNow;
            new EvidenceStore(DataDir).SaveRun(new() { ProjectSlug = slug, TicketId = ticket.Id.ToString(), CapturedAt = at,
                RunIds = [execution.Id], Status = EvidenceStatus.Complete,
                CommandsRun = [new("python -c unittest", null, process.ExitCode, !checksFailed, null, checksFailed ? 3 : 4, checksFailed ? 1 : 0, new("process-exit", "python-unittest", execution.Id, at, EvidenceTrust.Verified))] });
            return new ColumnDispatchResult(new AgentRun { RunId = execution.Id, ProjectSlug = slug, TicketId = ticket.Id,
                AgentName = processor.Name, SkillFile = "fixture", ConcurrencyGroup = "fixture", StartedAt = at, Status = AgentRunStatus.Completed },
                new("completed", processor.RequiredSkills, "Local verification process completed."), null);
        }
    }
}
