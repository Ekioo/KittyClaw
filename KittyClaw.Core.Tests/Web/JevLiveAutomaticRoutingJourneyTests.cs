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

public sealed class JevLiveAutomaticRoutingJourneyTests(ITestOutputHelper output)
{
    [LiveJevBrowserFact]
    public async Task Real_JEV_routes_above_threshold_and_leaves_lower_confidence_for_manual_validation()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KittyClaw-debug", "jev-live-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), """{"OnboardingSeen":true,"Language":"en"}""");
        var originalData = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        var originalUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", data);
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        try
        {
            await using var factory = new JevLiveApiJourneyTests.Factory(callBudget: 2);
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
            await Expect(dialog.GetByTestId("jev-automatic-routing")).Not.ToBeCheckedAsync();
            await dialog.GetByTestId("jev-automatic-routing").CheckAsync();
            await dialog.GetByTestId("jev-confidence-threshold").FillAsync("50");
            await dialog.Locator("label.workflow-check").Filter(new() { HasText = "Enabled" }).Locator("input").CheckAsync();
            await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(dialog).Not.ToBeVisibleAsync();
            var processor = await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json);
            Assert.True(processor!.JevPostRunObservation);
            Assert.False(processor.JevObservationOnly);
            Assert.True(processor.JevAutomaticRouting);
            Assert.Equal(0.5, processor.JevConfidenceThreshold);
            var observations = factory.Services.GetRequiredService<JevQualificationService>();
            var reviews = factory.Services.GetRequiredService<JevDecisionStore>();
            async Task<Ticket> CreateTicketAsync(string title)
            {
                var response = await http.PostAsJsonAsync($"/api/projects/{slug}/tickets",
                    new CreateTicketRequest(title, "owner", source.Name,
                        "Verification has failed. Repair the failure in Todo before submitting for Review.",
                        PipelineId: source.PipelineId, ColumnId: source.Id));
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<Ticket>(json))!;
            }
            async Task<JevRoutingProposal> WaitForProposalAsync(int ticketId)
            {
                var until = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < until)
                {
                    var result = (await observations.GetPostRunAsync(slug, ticketId)).SingleOrDefault();
                    if (result is not null)
                    {
                        Assert.Null(result.Error);
                        Assert.Equal($"column-{suggestion.Id}", result.Choice);
                        Assert.True(result.EvidenceAvailable);
                        Assert.False(string.IsNullOrWhiteSpace(result.Model));
                        return result;
                    }
                    await Task.Delay(500);
                }
                throw new TimeoutException("No live JEV proposal arrived within 60 seconds.");
            }

            var first = await CreateTicketAsync("Live JEV automatic repair route");
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{first.Id}").ToString());
            var firstProposal = await WaitForProposalAsync(first.Id);
            Assert.True(firstProposal.Confidence > 0.5, $"Live confidence {firstProposal.Confidence} did not exceed the configured 50% threshold.");
            await Expect(page.GetByTestId("jev-decision-history")).ToContainTextAsync("Automatically applied by JEV", new() { Timeout = 45000 });
            var applied = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{first.Id}", json))!;
            Assert.Equal(suggestion.Id, applied.ColumnId);
            Assert.Contains(applied.Activities, a => a.Author == "JEV" && a.Text.Contains("Review → Todo"));
            var decision = Assert.Single(await reviews.ListAsync(slug, first.Id));
            Assert.True(decision.Automatic); Assert.True(decision.Accepted);
            Assert.Equal(0.5, decision.ConfidenceThreshold); Assert.Equal(suggestion.Id, decision.AppliedTargetColumnId);
            Assert.Equal(JevDecisionStore.ProposalId(firstProposal), decision.ProposalId);
            await page.ReloadAsync();
            await Expect(page.GetByTestId("jev-decision-history")).ToContainTextAsync("Automatically applied by JEV");
            await page.GetByTestId("jev-decision-history").Locator("summary").ClickAsync();
            await page.GetByTestId("jev-decision-history").EvaluateAsync("element => element.scrollIntoView({ block: 'center' })");
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "live-above-threshold.png"), FullPage = true });

            // Raise the same source column's threshold through the UI before creating the second ticket.
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
            for (var i = 0; i < 20 && !await page.Locator(".column-context-menu-configure").IsVisibleAsync(); i++)
            {
                await page.Locator(".column-header").Filter(new() { HasText = "Backlog" }).ClickAsync(new() { Button = MouseButton.Right });
                await Task.Delay(500);
            }
            await page.Locator(".column-context-menu-configure").ClickAsync();
            dialog = page.GetByRole(AriaRole.Dialog);
            await dialog.Locator(".column-editor-tabs button").Nth(1).ClickAsync();
            await Expect(dialog.GetByTestId("jev-automatic-routing")).ToBeCheckedAsync();
            await dialog.GetByTestId("jev-confidence-threshold").FillAsync("100");
            await dialog.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(dialog).Not.ToBeVisibleAsync();
            processor = await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{source.Id}/processor", json);
            Assert.True(processor!.JevAutomaticRouting); Assert.Equal(1, processor.JevConfidenceThreshold);

            var second = await CreateTicketAsync("Live JEV repair route awaiting manual validation");
            await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}/ticket/{second.Id}").ToString());
            var secondProposal = await WaitForProposalAsync(second.Id);
            Assert.True(secondProposal.Confidence < 1, $"Live confidence {secondProposal.Confidence} did not fall below the configured 100% threshold.");
            var proposal = page.GetByTestId("jev-post-run-proposal");
            await Expect(proposal).ToContainTextAsync("Suggested destination: Todo", new() { Timeout = 20000 });
            await Expect(proposal.GetByTestId("jev-routing-policy")).ToContainTextAsync("100%");
            await Expect(proposal.GetByTestId("jev-accept")).ToBeEnabledAsync();
            var pending = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{second.Id}", json))!;
            Assert.Equal(actualTarget.Id, pending.ColumnId);
            var observationUntil = DateTime.UtcNow.AddSeconds(32);
            while (DateTime.UtcNow < observationUntil)
            {
                var current = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{second.Id}", json))!;
                Assert.Equal(actualTarget.Id, current.ColumnId);
                Assert.Equal(pending.UpdatedAt, current.UpdatedAt);
                Assert.Empty(await reviews.ListAsync(slug, second.Id));
                Assert.Equal(2, factory.Dispatcher.Count);
                await Task.Delay(1000);
            }
            await page.ReloadAsync();
            // Server prerendered markup can briefly disappear during hydration. Confirm a real
            // Blazor input round trip before asserting usable manual controls or taking a screenshot.
            var interactive = false;
            for (var attempt = 0; attempt < 5 && !interactive; attempt++)
            {
                await proposal.GetByTestId("jev-decision-reason").FillAsync("Manual review pending; keep this ticket in Review.");
                try
                {
                    await Expect(proposal.GetByTestId("jev-reject")).ToBeEnabledAsync(new() { Timeout = 2000 });
                    interactive = true;
                }
                catch (PlaywrightException) when (attempt < 4) { }
            }
            Assert.True(interactive);
            await Expect(proposal.GetByTestId("jev-accept")).ToBeEnabledAsync();
            await Expect(proposal.GetByTestId("jev-decision-reason")).ToHaveValueAsync("Manual review pending; keep this ticket in Review.");
            Assert.Empty(await reviews.ListAsync(slug, second.Id));
            var stillPending = (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{second.Id}", json))!;
            Assert.Equal(actualTarget.Id, stillPending.ColumnId); Assert.Equal(pending.UpdatedAt, stillPending.UpdatedAt);
            await Expect(page.GetByTestId("jev-decision-history")).Not.ToBeVisibleAsync();
            // A reload can replace this section while Blazor hydrates; use a synchronous DOM scroll.
            await proposal.EvaluateAsync("element => element.scrollIntoView({ block: 'center' })");
            await page.ScreenshotAsync(new() { Path = Path.Combine(data, "live-below-threshold-manual.png"), FullPage = true });
            Assert.Equal(suggestion.Id, (await http.GetFromJsonAsync<Ticket>($"/api/projects/{slug}/tickets/{first.Id}", json))!.ColumnId);
            Assert.Single(await reviews.ListAsync(slug, first.Id));
            Assert.Equal(2, factory.Handler.Count);
            Assert.Equal(2, factory.Dispatcher.Count);
            Assert.Empty(errors);
            output.WriteLine($"ABOVE: model={firstProposal.Model}, confidence={firstProposal.Confidence:P0}, threshold=50%, Review → Todo; automatic audit persisted.");
            output.WriteLine($"BELOW: model={secondProposal.Model}, confidence={secondProposal.Confidence:P0}, threshold=100%, stayed in Review for 32 seconds; manual acceptance enabled after reload, no extra agent.");
            output.WriteLine($"PASS: two real TypeSafe requests, detected machine credential, debug instance {http.BaseAddress}");
            output.WriteLine($"Screenshots and isolated debug data: {data}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", originalData);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalUrls);
        }
    }
}
