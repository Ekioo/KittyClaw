using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

[Collection("MCP endpoint environment")]
public sealed class TicketQuestionJourneyTests
{
    [Fact]
    public async Task Owner_selects_multiple_choices_in_existing_queue_and_processor_resumes()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_RUN_QUESTION_JOURNEY") != "1") return;
        using var temp = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "settings.json"), """{"OnboardingSeen":true,"Language":"fr"}""");
        using var factory = new Factory(temp.Path);
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var projects = factory.Services.GetRequiredService<ProjectService>();
        var project = await projects.CreateProjectAsync("Agent questions preview");
        await projects.TogglePauseAsync(project.Slug);
        var columns = factory.Services.GetRequiredService<ColumnService>();
        var source = await columns.CreateColumnAsync(project.Slug, "Ready");
        var target = await columns.CreateColumnAsync(project.Slug, "Review", role: ColumnRole.OwnerAction);
        var processors = factory.Services.GetRequiredService<ColumnProcessorService>();
        await processors.SaveAsync(project.Slug, source.Id, "Worker", "Work.", null, true, 20, [], [], [], defaultTargetColumnId: target.Id);
        var tickets = factory.Services.GetRequiredService<TicketService>();
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Choisir le périmètre des modifications",
            description: "Mettre à jour le code et vérifier les réglages du projet.", status: source.Name, columnId: source.Id);
        var executions = factory.Services.GetRequiredService<ColumnExecutionService>();
        var question = await executions.AskQuestionAsync(project.Slug, ticket.Id,
            new("Quels éléments puis-je modifier ?", ["Le code", "La configuration", "Les tests"], true, Author: "Worker"));
        await projects.TogglePauseAsync(project.Slug);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "msedge" });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1000 } });
        await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{project.Slug}").ToString());
        await Expect(page.Locator(".ticket-card .automation-question")).ToContainTextAsync("En attente de votre réponse");
        await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{project.Slug}/ticket/{ticket.Id}").ToString());
        var panel = page.Locator(".ticket-panel-thread .automation-question");
        await Expect(panel).ToContainTextAsync("Quels éléments puis-je modifier ?");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Le code", Exact = true }).ClickAsync();
        await panel.GetByRole(AriaRole.Button, new() { Name = "Les tests", Exact = true }).ClickAsync();
        await panel.Locator("textarea").FillAsync("Conserver les hooks Git.");
        await Expect(panel.Locator("button[aria-pressed='true']")).ToHaveCountAsync(2);
        var screenshotDir = Environment.GetEnvironmentVariable("KITTYCLAW_QUESTION_SCREENSHOT_DIR");
        if (screenshotDir is not null)
        {
            Directory.CreateDirectory(screenshotDir);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDir, "ticket-question-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await Expect(panel.Locator("button[aria-pressed='true']")).ToHaveCountAsync(2);
            await Expect(panel.Locator("textarea")).ToHaveValueAsync("Conserver les hooks Git.");
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDir, "ticket-question-mobile.png"), FullPage = true });
        }
        await panel.GetByRole(AriaRole.Button, new() { Name = "Répondre et reprendre" }).ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);
        var until = DateTime.UtcNow.AddSeconds(15);
        while ((await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId != target.Id && DateTime.UtcNow < until)
            await Task.Delay(100);
        Assert.Equal(target.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
        var answered = Assert.Single(await executions.ListQuestionsAsync(project.Slug, ticket.Id));
        Assert.Equal(new[] { "Le code", "Les tests" }, answered.SelectedOptions);
        Assert.Equal("Conserver les hooks Git.", answered.FreeText);
        Assert.Equal(ColumnExecutionStatus.Completed, (await executions.GetAsync(project.Slug, question.ExecutionId))!.Status);
        Assert.Equal(1, factory.Services.GetRequiredService<StubDispatcher>().Count);
    }

    private sealed class Factory(string data) : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("KITTYCLAW_DATA_DIR", data).UseSetting("KITTYCLAW_DESKTOP", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.AddSingleton<StubDispatcher>();
                services.AddSingleton<IColumnAgentDispatcher>(sp => sp.GetRequiredService<StubDispatcher>());
            });
        }
    }

    private sealed class StubDispatcher : IColumnAgentDispatcher
    {
        public int Count { get; private set; }
        public Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Count++;
            return Task.FromResult(new ColumnDispatchResult(new AgentRun { RunId = execution.Id, ProjectSlug = slug,
                TicketId = ticket.Id, AgentName = processor.Name, SkillFile = "", ConcurrencyGroup = "test", StartedAt = DateTime.UtcNow },
                new("completed", [], "Completed after the owner answered."), null));
        }
    }
}
