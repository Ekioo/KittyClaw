using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed class JevAutomaticRoutingJourneyTests(ITestOutputHelper output)
{
    [DebugBrowserFact]
    public async Task Automatic_opt_in_routes_above_threshold_and_lower_confidence_remains_manual()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KittyClaw-debug", "jev-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        var originalData = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        var originalUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", data);
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        try
        {
            await using var factory = new Factory();
            factory.UseKestrel(0);
            using var http = factory.CreateClient();
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            var created = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("JEV after agent"));
            created.EnsureSuccessStatusCode();
            var slug = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
            var workspace = Path.Combine(data, "workspace");
            Directory.CreateDirectory(workspace);
            var start = new System.Diagnostics.ProcessStartInfo("git")
            { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("init");
            using (var git = System.Diagnostics.Process.Start(start)!)
            {
                var stdout = git.StandardOutput.ReadToEndAsync(); var stderr = git.StandardError.ReadToEndAsync();
                await git.WaitForExitAsync(); Assert.True(git.ExitCode == 0, await stderr); await stdout;
            }
            (await http.PatchAsJsonAsync($"/api/projects/{slug}", new UpdateProjectRequest(WorkspacePath: workspace,
                RepositoryPath: workspace, WorktreesEnabled: false))).EnsureSuccessStatusCode();
            var columns = (await http.GetFromJsonAsync<List<BoardColumn>>($"/api/projects/{slug}/columns", json))!;
            var source = columns.Single(c => c.Name == "Backlog");
            var actualTarget = columns.Single(c => c.Name == "Review");
            var suggestion = columns.Single(c => c.Name == "Todo");
            var saved = await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{source.Id}/processor",
                new SaveColumnProcessorRequest("Worker", "Implement the ticket", Enabled: false,
                    DefaultTargetColumnId: actualTarget.Id, Routes: [new("needs_revision", suggestion.Id)]));
            saved.EnsureSuccessStatusCode();
            (await http.PutAsJsonAsync($"/api/projects/{slug}/secrets/TYPESAFE_API_KEY", new { value = "debug-only-key" })).EnsureSuccessStatusCode();
            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1100 } });
            var errors = new List<string>();
            page.PageError += (_, message) => errors.Add(message);
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
            for (var i = 0; i < 20 && !await page.Locator(".column-context-menu-configure").IsVisibleAsync(); i++)
            {
                await page.Locator(".column-header").Filter(new() { HasText = "Backlog" }).ClickAsync(new() { Button = MouseButton.Right });
                await Task.Delay(500);
            }
            await page.Locator(".column-context-menu-configure").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await dialog.Locator(".column-editor-tabs button").Nth(1).ClickAsync();
            await dialog.GetByTestId("jev-settings").Locator("summary").First.ClickAsync();
            await dialog.GetByLabel("JEV routing proposals after agent runs").CheckAsync();
            await dialog.Locator("label.workflow-check").Filter(new() { HasText = "Enabled" }).Locator("input").CheckAsync();
            await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(dialog).Not.ToBeVisibleAsync();
            var processor = await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json);
            Assert.True(processor!.JevPostRunObservation);
            Assert.False(processor.JevObservationOnly);
            var response = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Implement a bounded change", "owner", source.Name,
                    "The fixture agent completes the work; JEV should suggest a different route without moving it.",
                    PipelineId: source.PipelineId, ColumnId: source.Id));
            response.EnsureSuccessStatusCode();
            var ticket = (await response.Content.ReadFromJsonAsync<Ticket>(json))!;
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{ticket.Id}").ToString());
            var proposal = page.GetByTestId("jev-history-entry").Filter(new() { HasText = "Suggested destination: Todo" }).First;
            await Expect(proposal).ToContainTextAsync("Suggested destination: Todo", new() { Timeout = 60000 });
            await Expect(page.GetByTestId("ticket-current-column")).ToContainTextAsync("Review");
            await proposal.Locator("summary").ClickAsync();
            await Expect(proposal).ToContainTextAsync("jev-post-run-fixture");
            await proposal.ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "after-agent-routing.png"), FullPage = true });
            var until = DateTime.UtcNow.AddSeconds(32);
            while (DateTime.UtcNow < until)
            {
                var current = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{ticket.Id}", json))!;
                Assert.Equal(actualTarget.Id, current.ColumnId);
                await Task.Delay(1000);
            }
            Assert.Equal(1, factory.Dispatcher.Count);
            Assert.Equal(1, factory.Handler.Count);
            var executions = (await http.GetFromJsonAsync<List<ColumnExecution>>($"/api/projects/{slug}/column-executions", json))!;
            Assert.Equal(ColumnExecutionStatus.Completed, Assert.Single(executions).Status);
            // Explicit automatic opt-in applies the proposed move and lets the ordinary destination processor run.
            (await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{suggestion.Id}/processor",
                new SaveColumnProcessorRequest("Follow-up worker", "Handle the explicitly accepted ticket", DefaultTargetColumnId: actualTarget.Id))).EnsureSuccessStatusCode();
            // Turn on automatic routing explicitly in the column dialog, using an inclusive 100% threshold.
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
            for (var i = 0; i < 20 && !await page.Locator(".column-context-menu-configure").IsVisibleAsync(); i++)
            {
                await page.Locator(".column-header").Filter(new() { HasText = "Backlog" }).ClickAsync(new() { Button = MouseButton.Right });
                await Task.Delay(500);
            }
            await page.Locator(".column-context-menu-configure").ClickAsync();
            dialog = page.GetByRole(AriaRole.Dialog);
            await dialog.Locator(".column-editor-tabs button").Nth(1).ClickAsync();
            await dialog.GetByTestId("jev-settings").Locator("summary").First.ClickAsync();
            await Expect(dialog.GetByTestId("jev-automatic-routing")).Not.ToBeCheckedAsync();
            await dialog.GetByTestId("jev-automatic-routing").CheckAsync();
            await dialog.GetByTestId("jev-confidence-threshold").FillAsync("100");
            await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(dialog).Not.ToBeVisibleAsync();
            processor = await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json);
            Assert.True(processor!.JevAutomaticRouting); Assert.Equal(1, processor.JevConfidenceThreshold);
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{ticket.Id}").ToString());
            await Expect(page.GetByTestId("jev-history-entry").Filter(new() { HasText = "Suggested destination: Todo" }).First).ToContainTextAsync("Automatically applied by JEV", new() { Timeout = 45000 });
            var acceptedUntil = DateTime.UtcNow.AddSeconds(30);
            while (factory.Dispatcher.Count < 2 && DateTime.UtcNow < acceptedUntil) await Task.Delay(100);
            Assert.Equal(2, factory.Dispatcher.Count);
            var acceptedTicket = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{ticket.Id}", json))!;
            Assert.Contains(acceptedTicket.Activities, a => a.Author == "JEV" && a.Text.Contains("Review → Todo"));
            var reviews = factory.Services.GetRequiredService<JevDecisionStore>();
            var acceptedReview = Assert.Single(await reviews.ListAsync(slug, ticket.Id));
            Assert.True(acceptedReview.Accepted);
            Assert.True(acceptedReview.Automatic); Assert.Equal(1, acceptedReview.ConfidenceThreshold);
            Assert.Equal(suggestion.Id, acceptedReview.AppliedTargetColumnId);
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-history-entry").Filter(new() { HasText = "Suggested destination: Todo" }).First).ToContainTextAsync("Automatically applied by JEV", new() { Timeout = 45000 });
            await proposal.GetByTestId("jev-decision-history").Locator("summary").ClickAsync();
            await proposal.GetByTestId("jev-decision-history").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "jev-accepted-history.png"), FullPage = true });

            // A second ticket exercises functional rejection without launching another agent.
            factory.Handler.Confidence = 0.8;
            var secondResponse = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Inspect a rejected proposal", "owner", source.Name,
                    PipelineId: source.PipelineId, ColumnId: source.Id));
            secondResponse.EnsureSuccessStatusCode();
            var secondTicket = (await secondResponse.Content.ReadFromJsonAsync<Ticket>(json))!;
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{secondTicket.Id}").ToString());
            proposal = page.GetByTestId("jev-history-entry").Filter(new() { HasText = "Suggested destination: Todo" }).First;
            await Expect(proposal).ToContainTextAsync("Suggested destination: Todo", new() { Timeout = 60000 });
            Assert.Equal(1, (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json))!.JevConfidenceThreshold);
            var lowConfidenceUntil = DateTime.UtcNow.AddSeconds(32);
            while (DateTime.UtcNow < lowConfidenceUntil)
            {
                Assert.Equal(actualTarget.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{secondTicket.Id}", json))!.ColumnId);
                Assert.Equal(3, factory.Dispatcher.Count);
                Assert.Empty(await reviews.ListAsync(slug, secondTicket.Id));
                await Task.Delay(1000);
            }
            var rejectedProposal = Assert.Single(await factory.Services.GetRequiredService<JevQualificationService>().GetPostRunAsync(slug, secondTicket.Id));
            await factory.Services.GetRequiredService<JevProposalDecisionService>().DecideAsync(slug, secondTicket.Id,
                JevDecisionStore.ProposalId(rejectedProposal!), false, "Review evidence before returning to implementation");
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-history-entry").Filter(new() { HasText = "Suggested destination: Todo" }).First).ToContainTextAsync("Rejected");
            await Expect(page.GetByTestId("jev-history-entry").Filter(new() { HasText = "Suggested destination: Todo" }).First).ToContainTextAsync("Review evidence before returning to implementation");
            Assert.Equal(actualTarget.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{secondTicket.Id}", json))!.ColumnId);
            Assert.Equal(3, factory.Dispatcher.Count);
            Assert.Equal(2, factory.Handler.Count);
            var rejectedReview = Assert.Single(await reviews.ListAsync(slug, secondTicket.Id));
            Assert.False(rejectedReview.Accepted);
            Assert.Equal("Review evidence before returning to implementation", rejectedReview.Reason);
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-history-entry").Filter(new() { HasText = "Suggested destination: Todo" }).First).ToContainTextAsync("Rejected");
            await proposal.GetByTestId("jev-decision-history").Locator("summary").ClickAsync();
            await proposal.GetByTestId("jev-decision-history").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "jev-rejected-and-requalified.png"), FullPage = true });
            Assert.Empty(errors);
            output.WriteLine($"PASS: {http.BaseAddress} — manual default respected; explicit automatic opt-in started the destination processor; rejection produced an alternative with owner feedback.");
            output.WriteLine($"Screenshot and isolated debug data: {data}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", originalData);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalUrls);
        }
    }

    private sealed class Factory : WebApplicationFactory<CreateProjectRequest>
    {
        public JevPostRunObservationTests.PostRunHandler Handler { get; } = new();
        public FixtureDispatcher Dispatcher { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IColumnAgentDispatcher>(); services.AddSingleton<IColumnAgentDispatcher>(Dispatcher);
                services.RemoveAll<JevQualificationService>();
                services.AddSingleton(sp => new JevQualificationService(sp.GetRequiredService<ProjectService>(),
                    sp.GetRequiredService<ProjectSecretVault>(), new HttpClient(Handler), sp.GetRequiredService<JevConfigurationService>()));
            });
        }
    }

    private sealed class FixtureDispatcher : IColumnAgentDispatcher
    {
        public int Count;
        public Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            var run = new AgentRun { RunId = execution.Id, ProjectSlug = slug, TicketId = ticket.Id,
                AgentName = processor.Name, SkillFile = "fixture", ConcurrencyGroup = $"column-{processor.ColumnId}",
                StartedAt = DateTime.UtcNow, Status = AgentRunStatus.Completed };
            return Task.FromResult(new ColumnDispatchResult(run, new("completed", [], "Implementation complete; verification remains an agent claim."), null));
        }
    }
}
