using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class ColumnEditorPhasesJourneyTests
{
    [DebugBrowserFact]
    public async Task Entry_rules_precede_modes_and_optional_fallback_survives_save_and_reload()
    {
        using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
        factory.UseKestrel(0);
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Column phase editor"));
        response.EnsureSuccessStatusCode();
        var slug = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        await factory.Services.GetRequiredService<ProjectService>().TogglePauseAsync(slug);
        var columns = await factory.Services.GetRequiredService<ColumnService>().ListColumnsAsync(slug);
        var first = columns.First();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 1100 } });
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{slug}").ToString());
        var editor = page.Locator(".column-editor");
        async Task OpenAsync()
        {
            for (var attempt = 0; attempt < 20 && !await page.Locator(".column-context-menu-configure").IsVisibleAsync(); attempt++)
            {
                await page.Locator(".column-header").First.ClickAsync(new() { Button = MouseButton.Right });
                await Task.Delay(150);
            }
            await page.Locator(".column-context-menu-configure").ClickAsync();
            await Expect(editor.GetByTestId("column-mode-phase")).ToBeVisibleAsync();
        }
        async Task SaveAsync()
        {
            await editor.Locator(".processor-dialog-footer .settings-primary").ClickAsync();
            await Expect(editor).ToHaveCountAsync(0);
        }
        await OpenAsync();
        await Expect(editor.GetByTestId("column-mode-None")).ToBeCheckedAsync();
        await Expect(editor.GetByTestId("column-entry-phase")).ToBeVisibleAsync();
        await Expect(editor.Locator(".column-phase-number")).ToHaveTextAsync(["1", "2"]);
        await editor.GetByTestId("require-acceptance-criteria").CheckAsync();
        await editor.GetByTestId("require-validated-acceptance-criteria").CheckAsync();
        // An incomplete profile draft becomes dormant when selecting None.
        await editor.GetByTestId("column-mode-Agent").CheckAsync();
        await editor.GetByTestId("jev-profile-mode").CheckAsync();
        await editor.GetByTestId("column-mode-None").CheckAsync();
        await SaveAsync();
        Assert.Null(await factory.Services.GetRequiredService<ColumnProcessorService>().GetAsync(slug, first.Id));
        Assert.True((await factory.Services.GetRequiredService<ColumnService>().ListColumnsAsync(slug)).First().RequireValidatedAcceptanceCriteria);
        await OpenAsync();
        await editor.GetByTestId("column-mode-Agent").CheckAsync();
        await editor.Locator(".column-agent-identity textarea").First.FillAsync("Deliver the change and verify each requirement.");
        await editor.GetByTestId("evaluate-acceptance-criteria").CheckAsync();
        await Expect(editor.Locator(".column-phase-number")).ToHaveTextAsync(["1", "2", "3", "4", "5", "6", "7"]);
        // An empty PowerShell action must not be converted or saved without fallback.
        await editor.Locator(".processor-actions-heading button").First.ClickAsync();
        await editor.GetByTestId("column-mode-Triage").CheckAsync();
        await SaveAsync();
        var dormant = (await factory.Services.GetRequiredService<ColumnProcessorService>().GetAsync(slug, first.Id))!;
        Assert.Empty(dormant.BeforeActions);
        await OpenAsync();
        await Expect(editor.GetByTestId("jev-fallback-to-agent")).Not.ToBeCheckedAsync();
        await Expect(editor.GetByTestId("column-fallback-settings")).ToHaveCountAsync(0);
        await Expect(editor.GetByTestId("column-recovery")).ToHaveCountAsync(0);
        await Expect(editor.GetByTestId("column-entry-phase")).ToBeVisibleAsync();
        await editor.GetByTestId("jev-fallback-to-agent").CheckAsync();
        await editor.GetByTestId("column-fallback-settings").Locator("summary").First.ClickAsync();
        await editor.Locator(".column-agent-identity textarea").First.FillAsync("Deliver the change and verify each requirement.");
        await editor.GetByTestId("evaluate-acceptance-criteria").CheckAsync();
        await SaveAsync();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var saved = (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{first.Id}/processor", json))!;
        Assert.True(saved.JevObservationOnly);
        Assert.True(saved.JevFallbackToAgent);
        Assert.True(saved.EvaluateAcceptanceCriteria);
        await OpenAsync();
        await Expect(editor.GetByTestId("column-mode-Triage")).ToBeCheckedAsync();
        await Expect(editor.GetByTestId("jev-fallback-to-agent")).ToBeCheckedAsync();
        await editor.GetByTestId("column-mode-Agent").CheckAsync();
        await editor.Locator(".processor-actions-heading button").First.ClickAsync();
        // General routes are dormant in owner-review mode, including invalid duplicates.
        var addRoute = editor.Locator(".column-routing-fields > button");
        await addRoute.ClickAsync();
        await addRoute.ClickAsync();
        foreach (var row in await editor.Locator(".route-row").AllAsync())
        {
            await row.Locator("input").FillAsync("duplicate");
            await row.Locator("select").SelectOptionAsync(columns.Last().Id.ToString());
        }
        await editor.GetByTestId("column-mode-Review").CheckAsync();
        await editor.GetByTestId("jev-fallback-to-agent").UncheckAsync();
        await Expect(editor.GetByTestId("column-no-fallback")).ToBeVisibleAsync();
        await Expect(editor.GetByTestId("column-fallback-settings")).ToHaveCountAsync(0);
        await Expect(editor.Locator(".column-phase-number")).ToHaveTextAsync(["1", "2", "3", "4"]);
        await SaveAsync();
        saved = (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{first.Id}/processor", json))!;
        Assert.True(saved.JevAcceptanceValidation);
        Assert.False(saved.JevFallbackToAgent);
        await OpenAsync();
        await Expect(editor.GetByTestId("column-mode-Review")).ToBeCheckedAsync();
        await Expect(editor.GetByTestId("jev-fallback-to-agent")).Not.ToBeCheckedAsync();
        var screenshotDir = Path.Combine(FindRepoRoot(), "evidence", "column-editor-phases");
        Directory.CreateDirectory(screenshotDir);
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDir, "desktop.png") });
        await page.SetViewportSizeAsync(390, 844);
        await Expect(editor.GetByTestId("column-mode-phase")).ToBeVisibleAsync();
        Assert.True(await editor.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(screenshotDir, "mobile.png") });
        await editor.GetByTestId("column-mode-None").CheckAsync();
        await Expect(editor.GetByTestId("column-jev-analysis-phase")).ToHaveCountAsync(0);
        await Expect(editor.GetByTestId("column-jev-fallback")).ToHaveCountAsync(0);
        await Expect(editor.GetByTestId("require-acceptance-criteria")).ToBeCheckedAsync();
        await Expect(editor.GetByTestId("require-validated-acceptance-criteria")).ToBeCheckedAsync();
        await SaveAsync();
        saved = (await http.GetFromJsonAsync<ColumnProcessor>($"/api/projects/{slug}/columns/{first.Id}/processor", json))!;
        Assert.False(saved.Enabled);
        Assert.Equal("Deliver the change and verify each requirement.", saved.Mission);
        Assert.True(saved.EvaluateAcceptanceCriteria);
        // Visible fallback routes remain editable in owner-review mode.
        await OpenAsync();
        await editor.GetByTestId("column-mode-Review").CheckAsync();
        await editor.GetByTestId("jev-fallback-to-agent").CheckAsync();
        await editor.GetByTestId("column-fallback-settings").Locator("summary").First.ClickAsync();
        await editor.Locator(".column-routing-fields > button").ClickAsync();
        var fallbackRoute = editor.Locator(".route-row").Last;
        await fallbackRoute.Locator("input").FillAsync("fallback_completed");
        await fallbackRoute.Locator("select").SelectOptionAsync(columns.Last().Id.ToString());
        await SaveAsync();
        saved = (await factory.Services.GetRequiredService<ColumnProcessorService>().GetAsync(slug, first.Id))!;
        Assert.Contains(saved.Routes, route => route.Outcome == "fallback_completed" && route.TargetColumnId == columns.Last().Id);
        await OpenAsync();
        await editor.GetByTestId("column-fallback-settings").Locator("summary").First.ClickAsync();
        await Expect(editor.Locator(".route-row input")).ToHaveValueAsync("fallback_completed");
        Assert.Empty(errors);
    }

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(directory, "KittyClaw.slnx")))
            directory = Path.GetDirectoryName(directory)!;
        return directory;
    }
}
