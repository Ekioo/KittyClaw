using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Services;
using KittyClaw.Core.Evidence;
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

public sealed class JevLiveApiJourneyTests(ITestOutputHelper output)
{
    [LiveJevBrowserFact]
    public async Task Human_acceptance_runs_the_destination_processor_and_rejection_requalifies()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KittyClaw-debug", "jev-live-" + Guid.NewGuid().ToString("N"));
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
                new SaveColumnProcessorRequest("Worker", "Repair failing verification in Todo. Only successful verification permits Review.", Enabled: false,
                    DefaultTargetColumnId: actualTarget.Id, Routes: [new("verification_failed: Verified command failure requires implementation repairs in Todo", suggestion.Id)]));
            saved.EnsureSuccessStatusCode();
            var availability = await factory.Services.GetRequiredService<JevConfigurationService>().GetAsync(slug);
            Assert.True(availability.Available);
            Assert.Equal("environment", availability.Source);
            Assert.False(availability.ConfiguredInKittyClaw);
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
            await dialog.GetByLabel("JEV routing proposals after agent runs").CheckAsync();
            await dialog.Locator("label.workflow-check").Filter(new() { HasText = "Enabled" }).Locator("input").CheckAsync();
            await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(dialog).Not.ToBeVisibleAsync();
            var processor = await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json);
            Assert.True(processor!.JevPostRunObservation);
            Assert.False(processor.JevObservationOnly);
            var response = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Implement a bounded change", "owner", source.Name,
                    "Verification has failed. Repair the failure in Todo before submitting for Review.",
                    PipelineId: source.PipelineId, ColumnId: source.Id));
            response.EnsureSuccessStatusCode();
            var ticket = (await response.Content.ReadFromJsonAsync<Ticket>(json))!;
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{ticket.Id}").ToString());
            var proposal = page.GetByTestId("jev-post-run-proposal");
            await Expect(proposal).ToContainTextAsync("Suggested destination: Todo", new() { Timeout = 60000 });
            await Expect(proposal).ToContainTextAsync("Destination applied by the workflow: Review");
            await Expect(proposal).ToContainTextAsync("jev-");
            await Expect(proposal).Not.ToContainTextAsync("No independent evidence bundle");
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
            // Explicit acceptance applies the proposed move and lets the ordinary destination processor run.
            (await http.PutAsJsonAsync($"/api/projects/{slug}/columns/{suggestion.Id}/processor",
                new SaveColumnProcessorRequest("Follow-up worker", "Handle the explicitly accepted ticket", DefaultTargetColumnId: actualTarget.Id))).EnsureSuccessStatusCode();
            await proposal.GetByTestId("jev-accept").ClickAsync();
            await Expect(page.GetByTestId("jev-decision-history")).ToContainTextAsync("Accepted");
            var acceptedUntil = DateTime.UtcNow.AddSeconds(30);
            while (factory.Dispatcher.Count < 2 && DateTime.UtcNow < acceptedUntil) await Task.Delay(100);
            Assert.Equal(2, factory.Dispatcher.Count);
            var acceptedTicket = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{ticket.Id}", json))!;
            Assert.Contains(acceptedTicket.Activities, a => a.Author == "owner" && a.Text.Contains("Review → Todo"));
            var reviews = factory.Services.GetRequiredService<JevDecisionStore>();
            var acceptedReview = Assert.Single(await reviews.ListAsync(slug, ticket.Id));
            Assert.True(acceptedReview.Accepted);
            Assert.Equal(suggestion.Id, acceptedReview.AppliedTargetColumnId);
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-decision-history")).ToContainTextAsync("Accepted");
            await page.GetByTestId("jev-decision-history").Locator("summary").ClickAsync();
            await page.GetByTestId("jev-decision-history").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "jev-accepted-history.png"), FullPage = true });

            // A second ticket exercises functional rejection without launching another agent.
            var secondResponse = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                new CreateTicketRequest("Owner review required before implementation", "owner", source.Name,
                    "Verification has failed; repair in Todo before submitting for Review.",
                    PipelineId: source.PipelineId, ColumnId: source.Id));
            secondResponse.EnsureSuccessStatusCode();
            var secondTicket = (await secondResponse.Content.ReadFromJsonAsync<Ticket>(json))!;
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{secondTicket.Id}").ToString());
            proposal = page.GetByTestId("jev-post-run-proposal");
            await Expect(proposal).ToContainTextAsync("Suggested destination: Todo", new() { Timeout = 60000 });
            await proposal.GetByTestId("jev-decision-reason").FillAsync("Keep this ticket in Review for the owner to clarify requirements; do not return to Todo.");
            await proposal.GetByTestId("jev-reject").ClickAsync();
            await Expect(page.GetByTestId("jev-decision-history")).ToContainTextAsync("Rejected");
            var observations = factory.Services.GetRequiredService<JevQualificationService>();
            JevRoutingProposal? requalified = null;
            var requalifiedUntil = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < requalifiedUntil)
            {
                requalified = (await observations.GetPostRunAsync(slug, secondTicket.Id)).SingleOrDefault();
                if (requalified is not null && !requalified.Destinations.ContainsKey($"column-{suggestion.Id}")) break;
                await Task.Delay(500);
            }
            Assert.NotNull(requalified);
            Assert.Null(requalified.Error);
            Assert.False(requalified.Destinations.ContainsKey($"column-{suggestion.Id}"));
            Assert.NotEqual($"column-{suggestion.Id}", requalified.Choice);
            await Expect(proposal.GetByTestId("jev-decision-result")).Not.ToBeVisibleAsync(new() { Timeout = 20000 });
            await Expect(page.GetByTestId("jev-decision-history")).ToContainTextAsync("Keep this ticket in Review for the owner to clarify requirements; do not return to Todo.");
            Assert.Equal(actualTarget.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{secondTicket.Id}", json))!.ColumnId);
            Assert.Equal(3, factory.Dispatcher.Count);
            Assert.Equal(3, factory.Handler.Count);
            Assert.True(factory.Handler.SawOwnerFeedback);
            var rejectedReview = Assert.Single(await reviews.ListAsync(slug, secondTicket.Id));
            Assert.False(rejectedReview.Accepted);
            Assert.Equal("Keep this ticket in Review for the owner to clarify requirements; do not return to Todo.", rejectedReview.Reason);
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-decision-history")).ToContainTextAsync("Rejected");
            await page.GetByTestId("jev-decision-history").Locator("summary").ClickAsync();
            await page.GetByTestId("jev-decision-history").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "jev-rejected-and-requalified.png"), FullPage = true });
            foreach (var id in new[] { ticket.Id, secondTicket.Id })
                foreach (var result in await observations.GetPostRunAsync(slug, id))
                    output.WriteLine($"Live JEV: ticket={id}, model={result.Model}, choice={result.Choice}, confidence={result.Confidence}, usage={result.Usage}, error={result.Error}");
            Assert.Empty(errors);
            output.WriteLine($"PASS: {http.BaseAddress} — no automatic route; human acceptance started the destination processor; rejection excluded the destination and requalified with owner feedback.");
            output.WriteLine($"Screenshot and isolated debug data: {data}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", originalData);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalUrls);
        }
    }

    internal sealed class Factory(int callBudget = 3) : WebApplicationFactory<CreateProjectRequest>
    {
        public LiveHandler Handler { get; } = new(callBudget);
        public FixtureDispatcher Dispatcher { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IColumnAgentDispatcher>(); services.AddSingleton<IColumnAgentDispatcher>(sp => { Dispatcher.DataDir = sp.GetRequiredService<ProjectService>().DataDir; return Dispatcher; });
                services.RemoveAll<JevQualificationService>();
                services.AddSingleton(sp => new JevQualificationService(sp.GetRequiredService<ProjectService>(),
                    sp.GetRequiredService<ProjectSecretVault>(), new HttpClient(Handler), sp.GetRequiredService<JevConfigurationService>()));
            });
        }
    }

    // Real transport with a strict call budget. Never logs credentials or request/response bodies.
    internal sealed class LiveHandler : DelegatingHandler
    {
        public int Count;
        public bool SawOwnerFeedback;
        private readonly int _callBudget;
        public LiveHandler(int callBudget) : base(new HttpClientHandler { AllowAutoRedirect = false }) { _callBudget = callBudget; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Count) > _callBudget)
                throw new InvalidOperationException("Live JEV test call budget exceeded.");
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var feedback = payload.RootElement.GetProperty("state").GetProperty("context").GetProperty("ownerFeedback");
            if (feedback.ValueKind == JsonValueKind.Array && feedback.GetArrayLength() > 0)
            {
                var rejectedChoice = feedback[0].GetProperty("rejectedChoice").GetString()!;
                Assert.False(payload.RootElement.GetProperty("questions").GetProperty("routing").GetProperty("criteria").TryGetProperty(rejectedChoice, out _));
                Assert.Equal("Keep this ticket in Review for the owner to clarify requirements; do not return to Todo.", feedback[0].GetProperty("reason").GetString());
                SawOwnerFeedback = true;
            }
            return await base.SendAsync(request, ct);
        }
    }

    internal sealed class FixtureDispatcher : IColumnAgentDispatcher
    {
        public int Count;
        public string DataDir = null!;
        public async Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            var run = new AgentRun { RunId = execution.Id, ProjectSlug = slug, TicketId = ticket.Id,
                AgentName = processor.Name, SkillFile = "fixture", ConcurrencyGroup = $"column-{processor.ColumnId}",
                StartedAt = DateTime.UtcNow, Status = AgentRunStatus.Completed };
            // Run an intentionally invalid git command in the isolated fixture, then capture its actual exit.
            var start = new System.Diagnostics.ProcessStartInfo("git")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("kittyclaw-deliberately-invalid-verification-command");
            using var command = System.Diagnostics.Process.Start(start)!;
            var stdout = command.StandardOutput.ReadToEndAsync(ct);
            var stderr = command.StandardError.ReadToEndAsync(ct);
            await command.WaitForExitAsync(ct); await stdout; await stderr;
            Assert.NotEqual(0, command.ExitCode);
            new EvidenceStore(DataDir).SaveRun(new TicketEvidence
            {
                TicketId = ticket.Id.ToString(), ProjectSlug = slug, CapturedAt = DateTime.UtcNow,
                Status = EvidenceStatus.Complete, RunIds = [execution.Id],
                CommandsRun = [new("git fixture verification", null, command.ExitCode, false, null, null, null,
                    new("process-exit", "unknown", execution.Id, DateTime.UtcNow, EvidenceTrust.Verified))]
            });
            return new ColumnDispatchResult(run, new("completed", [], "Verification command failed; repair in Todo before Review."), null);
        }
    }
}

public sealed class LiveJevBrowserFactAttribute : FactAttribute
{
    public LiveJevBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_JEV_LIVE_E2E") != "1")
            Skip = "Opt-in paid live API journey: set KITTYCLAW_JEV_LIVE_E2E=1. Uses a detected machine key, isolated debug data and mock agents.";
    }
}
