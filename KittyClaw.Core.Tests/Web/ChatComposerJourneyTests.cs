using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Web.Api;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class ChatComposerJourneyTests
{
    [DebugBrowserFact]
    public async Task Rapid_typing_survives_server_renders_and_only_enter_invokes_submission()
    {
        using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var created = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Composer typing regression"));
        created.EnsureSuccessStatusCode();
        var slug = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
        for (var attempt = 0; attempt < 20 && !await page.Locator(".chat-drawer").IsVisibleAsync(); attempt++)
        {
            await page.Locator(".new-instruction-btn").ClickAsync();
            await Task.Delay(250);
        }
        await Expect(page.Locator(".chat-drawer")).ToBeVisibleAsync();
        await page.Locator(".chat-target-item.claude-direct, .chat-input-area textarea").First.WaitForAsync();
        if (await page.Locator(".chat-target-item.claude-direct").IsVisibleAsync())
            await page.Locator(".chat-target-item.claude-direct").ClickAsync();
        var input = page.Locator(".chat-input-area textarea");
        await Expect(input).ToBeEnabledAsync();
        await input.EvaluateAsync("""
            el => {
                el.__typingRefresh = setInterval(() => {
                    el.__enterDotnetRef?.invokeMethodAsync('OnImagePasteError', 'unsupported_type').catch(() => {});
                }, 30);
            }
            """);
        const string draft = "Une instruction complète, avec des caractères accentués et une saisie rapide sans perte.";
        try
        {
            await input.PressSequentiallyAsync(draft, new() { Delay = 8 });
            await Task.Delay(500);
            await Expect(input).ToHaveValueAsync(draft);
        }
        finally { await input.EvaluateAsync("el => clearInterval(el.__typingRefresh)"); }

        // Keep this a UI-only regression: intercept the bridge rather than dispatching a provider.
        var calls = await input.EvaluateAsync<string[]>("""
            el => {
                const previous = el.__enterDotnetRef;
                const calls = [];
                el.__enterDotnetRef = { invokeMethodAsync: (method, text) => {
                    calls.push(method + ':' + text); return Promise.resolve();
                }};
                try {
                    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true }));
                    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, bubbles: true }));
                    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true }));
                    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', repeat: true, bubbles: true }));
                    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
                    return calls;
                } finally { el.__enterDotnetRef = previous; }
            }
            """);
        Assert.Equal(["OnComposerEnter:" + draft], calls);
        await Expect(input).ToHaveValueAsync(draft);
    }
}
