using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class AcceptanceCriteriaPresentationJourneyTests
{
    private sealed class CaptureFactory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
            services.AddDataProtection().UseEphemeralDataProtectionProvider());
    }

    [DebugBrowserFact]
    public async Task LocalizedStatuses_PreserveEvidenceAndFitDesktopAndMobile()
    {
        using var data = new TempDir();
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), """{"OnboardingSeen":true,"Language":"fr"}""");
        var previousDataDir = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", data.Path);
        try
        {
            using var factory = new CaptureFactory();
            factory.UseKestrel(0);
            using var client = factory.CreateClient();
            var project = await factory.Services.GetRequiredService<ProjectService>().CreateProjectAsync("Présentation des critères");
            await factory.Services.GetRequiredService<ColumnService>().ListColumnsAsync(project.Slug);
            var tickets = factory.Services.GetRequiredService<TicketService>();
            var ticket = await tickets.CreateTicketAsync(project.Slug, "Ajouter un formulaire de contact", status: "Todo",
                description: "Permettre aux visiteurs d’envoyer un message depuis la page Contact.");
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1100 } });
            var ticketUrl = new Uri(client.BaseAddress!, $"/board/{project.Slug}/ticket/{ticket.Id}").ToString();
            await page.GotoAsync(ticketUrl);
            var section = page.GetByTestId("ticket-acceptance-criteria");
            await Expect(section.Locator(".acceptance-empty")).ToBeVisibleAsync();
            // Prerendered controls are visible before the Blazor circuit becomes interactive.
            for (var attempt = 0; attempt < 40 && !await section.Locator(".acceptance-editor").IsVisibleAsync(); attempt++)
            {
                await section.GetByRole(AriaRole.Button, new() { Name = "Ajouter un critère", Exact = true }).ClickAsync();
                await Task.Delay(250);
            }
            await Expect(section.Locator(".acceptance-editor")).ToBeVisibleAsync();
            await section.GetByLabel("Critère", new() { Exact = true }).FillAsync("Les champs obligatoires sont vérifiés");
            await section.GetByLabel("Preuve", new() { Exact = true }).Last.FillAsync("Test réussi : les erreurs attendues sont affichées.");
            await section.GetByRole(AriaRole.Button, new() { Name = "Enregistrer", Exact = true }).ClickAsync();
            await Expect(section.Locator(".acceptance-item")).ToHaveCountAsync(1);
            var first = section.Locator(".acceptance-item").First;
            await first.GetByRole(AriaRole.Combobox).SelectOptionAsync("PASS");
            await Expect(first).ToHaveClassAsync("acceptance-item acceptance-item--pass");
            await first.Locator("summary").ClickAsync();
            await Expect(first.Locator("pre")).ToContainTextAsync("les erreurs attendues");
            var saved = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.AcceptanceCriteria.Single();
            Assert.Equal(AcceptanceStatus.PASS, saved.Status);
            Assert.NotNull(saved.Evidence);
            await tickets.SaveAcceptanceCriterionAsync(project.Slug, ticket.Id, null, new("Le message est envoyé au destinataire", AcceptanceStatus.PARTIAL));
            await tickets.SaveAcceptanceCriterionAsync(project.Slug, ticket.Id, null, new("Une confirmation apparaît après l’envoi"));
            await tickets.SaveAcceptanceCriterionAsync(project.Slug, ticket.Id, null, new("Les envois répétés sont bloqués pour éviter les messages en double", AcceptanceStatus.FAIL));
            await page.ReloadAsync();
            await Expect(section.Locator(".acceptance-item")).ToHaveCountAsync(4);
            await Expect(section.Locator(".acceptance-count")).ToHaveTextAsync("1 / 4 validés");
            Assert.Equal(new[] { "Validé", "Partiel", "Non évalué", "Non validé" },
                await section.Locator("option:checked").AllTextContentsAsync());
            await section.Locator("summary").ClickAsync();
            var screenshotDir = Environment.GetEnvironmentVariable("KITTYCLAW_CRITERIA_SCREENSHOTS");
            foreach (var width in new[] { 1440, 320, 390, 430 })
            {
                await page.SetViewportSizeAsync(width, 1100);
                await section.ScrollIntoViewIfNeededAsync();
                Assert.True(await section.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"), $"Criteria overflow at {width}px");
                foreach (var row in await section.Locator(".acceptance-item").AllAsync())
                    Assert.True(await row.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"), $"Criterion overflow at {width}px");
                if (screenshotDir is not null)
                {
                    Directory.CreateDirectory(screenshotDir);
                    await section.ScreenshotAsync(new() { Path = Path.Combine(screenshotDir, $"criteria-{width}.png") });
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", previousDataDir); }
    }
}
