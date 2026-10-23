using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class TicketCreationJourneyTests
{
    [DebugBrowserFact]
    public async Task Failed_creation_preserves_description_and_reload_restores_draft()
    {
        using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Ticket draft regression"));
        response.EnsureSuccessStatusCode();
        var slug = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var columns = factory.Services.GetRequiredService<ColumnService>();
        var first = (await columns.ListColumnsAsync(slug)).First();
        await factory.Services.GetRequiredService<TicketService>()
            .CreateTicketAsync(slug, "Existing", status: first.Name, columnId: first.Id);
        await columns.UpdateColumnAsync(slug, first.Id, maxTickets: 1, capacityBlocking: true);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
        await OpenCreateAsync(page);
        var popup = page.Locator(".create-popup");
        await Expect(popup).ToBeVisibleAsync();
        const string title = "Recover my draft";
        var description = string.Concat(Enumerable.Repeat("A long description with accents: éà.\n", 100));
        await popup.Locator(".create-input").FillAsync(title);
        await popup.Locator(".create-textarea").FillAsync(description);
        await popup.Locator(".create-popup-footer button").Last.ClickAsync();
        await Expect(popup.Locator("[role=alert]")).ToBeVisibleAsync();
        await Expect(popup.Locator(".create-textarea")).ToHaveValueAsync(description);
        await page.ReloadAsync();
        await OpenCreateAsync(page);
        await Expect(popup.Locator(".create-input")).ToHaveValueAsync(title);
        await Expect(popup.Locator(".create-textarea")).ToHaveValueAsync(description);
        await columns.UpdateColumnAsync(slug, first.Id, maxTickets: 0);
        await popup.Locator(".create-popup-footer button").Last.ClickAsync();
        await Expect(popup).ToHaveCountAsync(0);
        Assert.Contains(await factory.Services.GetRequiredService<TicketService>().ListTicketsAsync(slug), t => t.Title == title);
        await page.Locator(".add-ticket-btn").First.ClickAsync();
        await Expect(popup.Locator(".create-input")).ToHaveValueAsync("");
        await Expect(popup.Locator(".create-textarea")).ToHaveValueAsync("");
        await popup.Locator(".cancel-btn").ClickAsync();
        await page.Locator(".column-header").First.ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".column-context-menu-configure").ClickAsync();
        var editor = page.Locator(".column-editor");
        await Expect(editor).ToBeVisibleAsync();
        await editor.GetByLabel("Maximum tickets (0 = unlimited)", new() { Exact = true }).FillAsync("2");
        await editor.GetByLabel("When the limit is reached", new() { Exact = true }).SelectOptionAsync("false");
        await editor.GetByTestId("require-acceptance-criteria").CheckAsync();
        await editor.GetByTestId("require-validated-acceptance-criteria").CheckAsync();
        await editor.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
        await Expect(editor).ToHaveCountAsync(0);
        var saved = (await columns.ListColumnsAsync(slug)).First();
        Assert.Equal(2, saved.MaxTickets);
        Assert.False(saved.CapacityBlocking);
        Assert.True(saved.RequireAcceptanceCriteria);
        Assert.True(saved.RequireValidatedAcceptanceCriteria);
        await Expect(page.Locator(".column-capacity--full").First).ToContainTextAsync("2 / 2");
        await page.Locator(".column-header").First.ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator(".column-context-menu-configure").ClickAsync();
        await Expect(editor.GetByTestId("require-acceptance-criteria")).ToBeCheckedAsync();
        await Expect(editor.GetByTestId("require-validated-acceptance-criteria")).ToBeCheckedAsync();

    }
    private static async Task OpenCreateAsync(IPage page)
    {
        for (var attempt = 0; attempt < 20 && !await page.Locator(".create-popup").IsVisibleAsync(); attempt++)
        {
            await page.Locator(".add-ticket-btn").First.ClickAsync();
            await Task.Delay(250);
        }
    }
}
