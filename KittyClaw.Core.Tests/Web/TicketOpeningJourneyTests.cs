using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Tests.Services;
using KittyClaw.Web.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class TicketOpeningJourneyTests(ITestOutputHelper output)
{
    [DebugBrowserFact]
    public async Task Existing_jev_review_survives_interactive_startup_and_its_controls_work()
    {
        using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var projects = factory.Services.GetRequiredService<ProjectService>();
        var project = await projects.CreateProjectAsync("JEV opening continuity");
        await projects.TogglePauseAsync(project.Slug);
        var columns = await factory.Services.GetRequiredService<ColumnService>().ListColumnsAsync(project.Slug);
        var source = columns.First();
        var tickets = factory.Services.GetRequiredService<TicketService>();
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Review remains visible", status: source.Name,
            pipelineId: source.PipelineId, columnId: source.Id);
        var processor = await factory.Services.GetRequiredService<ColumnProcessorService>().SaveAsync(
            project.Slug, source.Id, "Evidence review", "Review the existing evidence", null, true, 20, [], [], [],
            jevAcceptanceValidation: true, jevAcceptanceCriteria: ["Opening remains responsive"]);
        var at = DateTime.UtcNow;
        new EvidenceStore(projects.DataDir).SaveTicket(new()
        {
            TicketId = ticket.Id.ToString(), ProjectSlug = project.Slug, CapturedAt = at,
            Status = EvidenceStatus.Complete, RunIds = ["opening-proof"],
            CommandsRun = [new("fixture tests", null, 0, true, null, 1, 0,
                new("process-exit", "unknown", "opening-proof", at, EvidenceTrust.Verified))]
        });
        using var mockClient = new HttpClient(new JevWorkReviewTests.Handler());
        var configuration = new JevConfigurationService(factory.Services.GetRequiredService<ProjectSecretVault>(),
            projects.DataDir, () => "debug-only-fixture");
        var reviews = new JevWorkReviewService(projects, configuration, mockClient);
        await reviews.ObserveAsync(project.Slug, processor, ticket, CancellationToken.None);
        Assert.True(processor.Enabled);
        Assert.True(processor.JevAcceptanceValidation);
        var stored = await factory.Services.GetRequiredService<JevWorkReviewService>().GetAsync(project.Slug, ticket.Id, source.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.Error);
        Assert.Equal("Opening remains responsive", Assert.Single(stored.Criteria).Criterion);
        var url = new Uri(http.BaseAddress!, $"/board/{project.Slug}/ticket/{ticket.Id}").ToString();
        Assert.Contains("Opening remains responsive", await http.GetStringAsync(url));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.AddInitScriptAsync("""
            window.reviewContinuity = { seen: false, missingFrames: 0 };
            function sampleReview() {
                const visible = !!document.querySelector('[data-testid="jev-criteria-results"]');
                if (window.reviewContinuity.seen && !visible) window.reviewContinuity.missingFrames++;
                if (visible) window.reviewContinuity.seen = true;
                requestAnimationFrame(sampleReview);
            }
            requestAnimationFrame(sampleReview);
            """);
        await page.GotoAsync(url);
        var review = page.GetByTestId("jev-work-review");
        await Expect(review.GetByTestId("jev-criteria-results")).ToContainTextAsync("Opening remains responsive");
        // Retry only while the server-rendered controls are waiting for the circuit to connect.
        for (var attempt = 0; attempt < 20 && !await page.GetByTestId("jev-work-reason").IsVisibleAsync(); attempt++)
        {
            await review.GetByTestId("jev-work-reject").ClickAsync();
            await page.WaitForTimeoutAsync(250);
        }
        await Expect(page.GetByTestId("jev-work-reason")).ToBeVisibleAsync();
        await review.GetByTestId("jev-work-cancel").ClickAsync();
        await Expect(page.GetByTestId("jev-work-reason")).ToHaveCountAsync(0);
        Assert.Equal(0, await page.EvaluateAsync<int>("window.reviewContinuity.missingFrames"));
    }

    [DebugBrowserFact]
    public async Task Direct_link_keeps_ticket_visible_while_viewport_module_is_loading()
    {
        using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var projects = factory.Services.GetRequiredService<ProjectService>();
        var project = await projects.CreateProjectAsync("Slow viewport regression");
        var ticket = await factory.Services.GetRequiredService<TicketService>()
            .CreateTicketAsync(project.Slug, "Visible before JavaScript");
        var url = new Uri(http.BaseAddress!, $"/board/{project.Slug}/ticket/{ticket.Id}").ToString();

        // Prerendered HTML must include the panel, even when JavaScript is unavailable.
        var html = await http.GetStringAsync(url);
        Assert.Contains("ticket-panel-desktop", html);
        Assert.Contains(ticket.Title, html);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.AddInitScriptAsync("""
            window.ticketContinuity = { seen: false, missingFrames: 0 };
            function sampleTicket() {
                const visible = !!document.querySelector('.ticket-panel');
                if (window.ticketContinuity.seen && !visible) window.ticketContinuity.missingFrames++;
                if (visible) window.ticketContinuity.seen = true;
                requestAnimationFrame(sampleTicket);
            }
            requestAnimationFrame(sampleTicket);
            """);
        var moduleRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModule = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/js/ticket-panel-viewport*", async route =>
        {
            moduleRequested.TrySetResult();
            await releaseModule.Task;
            await route.ContinueAsync();
        });
        try
        {
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await moduleRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var panel = page.Locator(".ticket-panel");
            await Expect(panel).ToBeVisibleAsync();
            await Expect(panel.Locator("h2")).ToContainTextAsync(ticket.Title);
            // Confirm this remains visible after the interactive circuit takes over.
            await page.WaitForTimeoutAsync(1500);
            await Expect(panel).ToBeVisibleAsync();
            Assert.Equal(0, await page.EvaluateAsync<int>("window.ticketContinuity.missingFrames"));
        }
        finally { releaseModule.TrySetResult(); }

        // The observer still selects the dedicated mobile view after it connects.
        await page.SetViewportSizeAsync(390, 844);
        await Expect(page.Locator(".ticket-panel-mobile h2")).ToContainTextAsync(ticket.Title);
        await page.SetViewportSizeAsync(1440, 900);
        await Expect(page.Locator(".ticket-panel-desktop h2")).ToContainTextAsync(ticket.Title);
    }

    [DebugBrowserFact]
    public async Task Ticket_opens_promptly_and_worktree_summary_follows_project_setting()
    {
        using var temp = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "integration");
        using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Ticket opening regression"));
        response.EnsureSuccessStatusCode();
        var slug = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var projects = factory.Services.GetRequiredService<ProjectService>();
        await projects.UpdateProjectAsync(slug, repository, repositoryPath: repository,
            worktreesEnabled: false, integrationBranch: "integration");
        var ticket = await factory.Services.GetRequiredService<TicketService>().CreateTicketAsync(slug, "Open this ticket");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error" && message.Text.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase))
                errors.Add(message.Text);
        };
        await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
        var card = page.Locator($".ticket-card[data-ticket-id='{ticket.Id}']");
        var panel = page.Locator(".ticket-panel");
        // Warm up the circuit before measuring a user click.
        for (var attempt = 0; attempt < 20 && !await panel.IsVisibleAsync(); attempt++)
        {
            await card.ClickAsync();
            await Task.Delay(250);
        }
        await Expect(panel).ToHaveAttributeAsync("aria-busy", "false");
        await Expect(page.GetByTestId("ticket-worktree-summary")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("ticket-worktree-error")).ToHaveCountAsync(0);
        await panel.Locator(".ticket-panel-header .close-btn").ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);
        // Closing while secondary details load must not break the next opening.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await card.ClickAsync();
            await panel.Locator(".ticket-panel-header .close-btn").ClickAsync();
            await Expect(panel).ToHaveCountAsync(0);
        }
        var stopwatch = Stopwatch.StartNew();
        await card.ClickAsync();
        await Expect(panel.Locator("h2")).ToContainTextAsync(ticket.Title, new() { Timeout = 3000 });
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Ticket opening took {stopwatch.Elapsed}.");
        output.WriteLine($"Warm ticket opening: {stopwatch.ElapsedMilliseconds} ms.");
        await Expect(panel).ToHaveAttributeAsync("aria-busy", "false");

        await projects.UpdateProjectAsync(slug, repository, worktreesEnabled: true, integrationBranch: "integration");
        await page.ReloadAsync();
        await Expect(panel).ToHaveAttributeAsync("aria-busy", "false");
        await Expect(page.GetByTestId("ticket-worktree-summary")).ToBeVisibleAsync();
        await projects.UpdateProjectAsync(slug, repository, worktreesEnabled: false);
        await page.ReloadAsync();
        await Expect(panel).ToHaveAttributeAsync("aria-busy", "false");
        await Expect(page.GetByTestId("ticket-worktree-summary")).ToHaveCountAsync(0);
        await Expect(page.GetByTestId("ticket-worktree-error")).ToHaveCountAsync(0);
        Assert.Empty(errors);
    }
}
