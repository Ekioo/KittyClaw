using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class TicketIntakeDebugJourneyTests
{
    [DebugBrowserFact]
    public async Task Assisted_creation_preserves_project_language_labels_and_dedicated_criteria()
    {
        using var tmp = new TempDir();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var mock = Path.Combine(root, "KittyClaw.ClaudeMock/bin/Debug/net10.0/claude.exe");
        Assert.True(File.Exists(mock), "Build KittyClaw.ClaudeMock before running this journey.");
        var variables = new Dictionary<string, string?>
        {
            ["KITTYCLAW_CODEX_BIN"] = mock,
            ["KITTYCLAW_CLAUDE_BIN"] = mock,
            ["KITTYCLAW_GROK_BIN"] = mock,
            ["KITTYCLAW_MISTRAL_BIN"] = mock,
            ["KITTYCLAW_PROVIDER_ALLOWLIST"] = "codex",
            ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = tmp.Path,
            ["KITTYCLAW_MOCK_SCENARIO"] = "ticket-intake-fr"
        };
        var previous = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in variables) Environment.SetEnvironmentVariable(key, value);
        try
        {
            using var factory = new Api.TicketLabelsPatchTests.ApiFactory();
            factory.UseKestrel(5232);
            using var http = factory.CreateClient();
            var workspace = Path.Combine(tmp.Path, "workspace");
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"),
                "Write project tickets in French. This project exports filtered data as CSV.");
            var response = await http.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Vérification assistant français"));
            response.EnsureSuccessStatusCode();
            var slug = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
            await factory.Services.GetRequiredService<ProjectService>().UpdateProjectAsync(slug, workspace);
            var labels = factory.Services.GetRequiredService<LabelService>();
            var relevant = await labels.CreateLabelAsync(slug, "Export", "#22aa66");
            var unrelated = await labels.CreateLabelAsync(slug, "Authentification", "#aa2266");
            var reply = JsonSerializer.Serialize(new
            {
                message = "Le ticket est prêt.", ready = true,
                title = "Exporter les résultats filtrés en CSV",
                description = "Ajouter un export CSV des résultats en conservant les filtres actifs.",
                acceptanceCriteria = new[] { "Le fichier CSV contient uniquement les résultats correspondant aux filtres actifs." },
                labelIds = new[] { relevant.Id, relevant.Id, 999999 }
            });
            var events = new object[]
            {
                new { type = "thread.started", thread_id = "mock-intake-fr" },
                new { type = "turn.started" },
                new { type = "item.completed", item = new { type = "agent_message", text = reply } },
                new { type = "turn.completed", usage = new { input_tokens = 10, output_tokens = 10 } }
            };
            await File.WriteAllLinesAsync(Path.Combine(tmp.Path, "ticket-intake-fr.ndjson"), events.Select(e => JsonSerializer.Serialize(e)));
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync(new() { Locale = "fr-FR", ViewportSize = new() { Width = 1440, Height = 1100 } });
            page.SetDefaultTimeout(30000);
            await page.GotoAsync($"http://localhost:5232/board/{slug}");
            for (var attempt = 0; attempt < 20 && !await page.Locator("#ticket-intake-input").IsVisibleAsync(); attempt++)
            {
                await page.Locator(".add-ticket-btn").First.ClickAsync();
                await Task.Delay(250);
            }
            await page.Locator("#ticket-intake-input").FillAsync("Ajouter un export CSV qui conserve les filtres actifs.");
            await page.Locator("#ticket-intake-input").PressAsync("Enter");
            await Expect(page.Locator(".ticket-open-btn").Filter(new() { HasText = "Exporter les résultats filtrés en CSV" })).ToBeVisibleAsync(new() { Timeout = 60000 });
            var tickets = factory.Services.GetRequiredService<TicketService>();
            var created = Assert.Single(await tickets.ListTicketsAsync(slug));
            var saved = await tickets.GetTicketAsync(slug, created.Id);
            Assert.NotNull(saved);
            Assert.Equal("Exporter les résultats filtrés en CSV", saved.Title);
            Assert.Equal("Ajouter un export CSV des résultats en conservant les filtres actifs.", saved.Description);
            Assert.Equal(relevant.Id, Assert.Single(saved.Labels).Id);
            Assert.DoesNotContain(saved.Labels, l => l.Id == unrelated.Id);
            var criterion = Assert.Single(saved.AcceptanceCriteria);
            Assert.Equal("Le fichier CSV contient uniquement les résultats correspondant aux filtres actifs.", criterion.Text);
            Assert.Equal(KittyClaw.Core.Models.AcceptanceStatus.Pending, criterion.Status);
            Assert.Null(criterion.Evidence);
            await page.Locator(".ticket-open-btn").Filter(new() { HasText = saved.Title }).ClickAsync();
            await Expect(page.Locator(".acceptance-text").First).ToHaveTextAsync(criterion.Text);
            await Expect(page.Locator(".ticket-desc").First).ToHaveTextAsync(saved.Description);
            var evidence = Environment.GetEnvironmentVariable("KITTYCLAW_DEBUG_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "ticket-intake-fr.png"), FullPage = true });
                await File.WriteAllTextAsync(Path.Combine(evidence, "ticket-intake-fr.json"), JsonSerializer.Serialize(new
                {
                    instance = "http://localhost:5232", provider = "mock-codex", saved.Title, saved.Description,
                    labels = saved.Labels.Select(l => new { l.Id, l.Name }), saved.AcceptanceCriteria,
                    checks = new[] { "French content persisted", "Relevant existing label only", "Unknown and duplicate label IDs discarded", "Criteria rendered in dedicated section", "Description excludes criteria" }
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }
}
