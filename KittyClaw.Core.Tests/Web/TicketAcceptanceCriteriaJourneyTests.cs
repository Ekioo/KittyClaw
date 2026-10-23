using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Web.Api;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class TicketAcceptanceCriteriaJourneyTests
{
    [AcceptanceCriteriaBrowserFact]
    public async Task OwnerAddsCriterionChangesStatusAndConsultsProofBelowDescription()
    {
        using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("acceptance-ui-" + Guid.NewGuid().ToString("N")[..8]));
        response.EnsureSuccessStatusCode();
        var slug = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();
        var created = await client.PostAsJsonAsync($"/api/projects/{slug}/tickets", new CreateTicketRequest("Feature", "owner", "Backlog", Description: "Description above criteria"));
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(client.BaseAddress!, $"/board/{slug}/ticket/{id}").ToString());
        var area = page.GetByTestId("ticket-acceptance-criteria");
        await Expect(area).ToBeVisibleAsync();
        async Task OpenEditor(string title)
        {
            for (var attempt = 0; attempt < 20 && !await area.Locator(".acceptance-editor").IsVisibleAsync(); attempt++)
            {
                await area.GetByTitle(title, new() { Exact = true }).ClickAsync();
                await Task.Delay(250);
            }
            await Expect(area.Locator(".acceptance-editor")).ToBeVisibleAsync();
        }
        await OpenEditor("Add criterion");
        await area.Locator(".acceptance-editor textarea").First.FillAsync("Login works");
        await area.Locator(".acceptance-editor select").SelectOptionAsync("application/json");
        await area.Locator("textarea[aria-label='Evidence']").FillAsync("{\"passed\":true}");
        await area.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        var status = area.GetByLabel("Status: Login works", new() { Exact = true });
        await Expect(status).ToHaveValueAsync("NotEvaluated");
        await status.SelectOptionAsync("PARTIAL");
        await Expect(status).ToHaveValueAsync("PARTIAL");
        await area.Locator("summary").ClickAsync();
        await Expect(area.Locator("pre")).ToHaveTextAsync("{\"passed\":true}");
        await page.ReloadAsync();
        await Expect(area.GetByLabel("Status: Login works", new() { Exact = true })).ToHaveValueAsync("PARTIAL");
        await page.WaitForFunctionAsync("""
            () => {
                const description = document.querySelector('.ticket-desc');
                const criteria = document.querySelector('[data-testid="ticket-acceptance-criteria"]');
                if (!description || !criteria) return false;
                const above = description.getBoundingClientRect(), below = criteria.getBoundingClientRect();
                return above.height > 0 && below.height > 0 && below.top >= above.bottom;
            }
            """);
        await OpenEditor("Edit criterion and evidence");
        await area.Locator(".acceptance-editor select").SelectOptionAsync("image/png");
        await area.Locator("input[type=file]").SetInputFilesAsync(new FilePayload {
            Name = "proof.png", MimeType = "image/png",
            Buffer = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=")
        });
        await Expect(area.Locator(".acceptance-preview")).ToBeVisibleAsync();
        await area.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await area.Locator("summary").ClickAsync();
        await Expect(area.Locator(".acceptance-proof img")).ToBeVisibleAsync();
        var stored = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tickets/{id}");
        Assert.Equal("image/png", stored.GetProperty("acceptanceCriteria")[0].GetProperty("evidence").GetProperty("mediaType").GetString());
        await OpenEditor("Edit criterion and evidence");
        await area.GetByRole(AriaRole.Button, new() { Name = "Remove evidence", Exact = true }).ClickAsync();
        await area.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(area.Locator("summary")).ToHaveCountAsync(0);
        await area.GetByTitle("Delete criterion", new() { Exact = true }).ClickAsync();
        await Expect(area.Locator(".acceptance-row")).ToHaveCountAsync(0);
    }
}

public sealed class AcceptanceCriteriaBrowserFactAttribute : FactAttribute
{
    public AcceptanceCriteriaBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KITTYCLAW_ACCEPTANCE_E2E") != "1")
            Skip = "Set KITTYCLAW_ACCEPTANCE_E2E=1 to run the isolated browser journey.";
    }
}
