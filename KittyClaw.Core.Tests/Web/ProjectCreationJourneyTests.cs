using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.QaRunner;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace KittyClaw.Core.Tests.Web;

public sealed class ProjectCreationJourneyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Create_project_through_browser_persists_workspace_and_first_ticket(
        bool existingRepository, bool retryWithoutGitConsent)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"kittyclaw-creation-journey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            if (existingRepository)
            {
                var init = await ProcessRunner.RunAsync("git", "init", workspace, TimeSpan.FromSeconds(10));
                Assert.True(init.Success, init.Stderr);
                await File.WriteAllBytesAsync(Path.Combine(workspace, "main.kittyclaw-pipeline"),
                    Services.PipelineImportServiceTests.BuildKit());
            }

            // A fresh server, registry and mock-only provider for each journey. No project
            // or ticket is seeded: these must all be created by the browser interactions.
            await using var instance = await TestInstance.StartAsync(WebExecutable());
            using var http = new HttpClient { BaseAddress = new Uri(instance.ApiUrl) };
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(instance.ApiUrl);
            await page.GetByTestId("create-project-open").ClickAsync();
            await page.GetByTestId("create-project-name").FillAsync("Creation journey");

            // Reproduce Browse -> type a directory -> Open -> Select this folder.
            await page.Locator(".project-creation .settings-browse").ClickAsync();
            await page.Locator(".folder-picker-path").FillAsync(workspace);
            await page.Locator(".folder-picker-path-row button").ClickAsync();
            await Expect(page.Locator(".folder-picker-selection strong")).ToHaveTextAsync(workspace);
            await page.Locator(".folder-picker-footer .settings-primary").ClickAsync();
            await Expect(page.GetByTestId("create-project-workspace")).ToHaveValueAsync(workspace);
            const string objective = "Deliver the first useful result";
            var gitOption = page.GetByTestId("create-project-git-init");
            if (existingRepository)
                await Expect(gitOption).ToHaveCountAsync(0);
            else
                await Expect(gitOption).ToBeCheckedAsync();

            await Expect(page.Locator("#first-objective")).ToHaveCountAsync(0);
            await page.GetByTestId("create-project-next").ClickAsync();
            await page.Locator("#first-objective").FillAsync(objective);

            if (retryWithoutGitConsent)
            {
                await page.GetByText("Back", new() { Exact = true }).ClickAsync();
                await gitOption.UncheckAsync();
                await page.GetByTestId("create-project-next").ClickAsync();
                await page.GetByTestId("create-project-initialize").ClickAsync();
                await Expect(page.Locator(".project-creation .settings-status-err")).ToContainTextAsync("not a Git repository");

                await Expect(page.Locator("#first-objective")).ToHaveValueAsync(objective);
                Assert.Empty(await http.GetFromJsonAsync<JsonElement[]>("/api/projects") ?? []);
                Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
                await page.GetByText("Back", new() { Exact = true }).ClickAsync();
                await gitOption.CheckAsync();
                await page.GetByTestId("create-project-next").ClickAsync();
            }

            await page.GetByTestId("create-project-initialize").ClickAsync();
            if (existingRepository)
            {
                await Expect(page.GetByTestId("workspace-pipeline-confirmation")).ToContainTextAsync("main.kittyclaw-pipeline");
                Assert.Empty(await http.GetFromJsonAsync<JsonElement[]>("/api/projects") ?? []);
                await page.GetByTestId("workspace-pipeline-decline").ClickAsync();
            }
            await Expect(page.Locator("#first-ticket-title")).ToHaveValueAsync(objective, new() { Timeout = 30_000 });
            Assert.True(Directory.Exists(Path.Combine(workspace, ".git")));
            Assert.True(File.Exists(Path.Combine(workspace, "AGENTS.md")));
            var project = await http.GetFromJsonAsync<JsonElement>("/api/projects/creation-journey");
            Assert.Equal(workspace, project.GetProperty("workspacePath").GetString());
            Assert.Single(await http.GetFromJsonAsync<JsonElement[]>("/api/projects") ?? []);

            const string title = "Owner reviewed first ticket";
            await page.Locator("#first-ticket-title").FillAsync(title);
            await page.Locator("#confirm-first-ticket").ClickAsync();
            await Expect(page.Locator(".migration-wizard")).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await page.Locator(".migration-wizard-header .agent-close-btn").ClickAsync();
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/board/creation-journey$"));
            await page.ReloadAsync();
            await page.Locator(".pipeline-tab").Filter(new() { HasText = "First success" }).ClickAsync();
            await Expect(page.Locator(".ticket-card").Filter(new() { HasText = title }).First).ToBeVisibleAsync(new() { Timeout = 30_000 });
            var tickets = await http.GetFromJsonAsync<JsonElement[]>("/api/projects/creation-journey/tickets");
            var ticket = Assert.Single(tickets!);
            Assert.Equal(title, ticket.GetProperty("title").GetString());
            Assert.Equal("owner", ticket.GetProperty("createdBy").GetString());
            Assert.Contains(objective, ticket.GetProperty("description").GetString());
            var git = await http.GetFromJsonAsync<JsonElement>("/api/projects/creation-journey/git");
            Assert.True(git.GetProperty("isRepository").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                // Git object files are read-only on Windows.
                foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Skip_setup_asks_before_importing_existing_kits(bool hasKit, bool acceptImport, bool approveActions)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"kittyclaw-empty-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var kitPath = Path.Combine(workspace, "main.kittyclaw-pipeline");
        if (hasKit)
            await File.WriteAllBytesAsync(kitPath, Services.PipelineImportServiceTests.BuildKit(riskyActions: true, pythonScheduledTask: true));
        try
        {
            await using var instance = await TestInstance.StartAsync(WebExecutable());
            using var http = new HttpClient { BaseAddress = new Uri(instance.ApiUrl) };
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(instance.ApiUrl);
            await page.GetByTestId("create-project-open").ClickAsync();
            await page.GetByTestId("create-project-name").FillAsync("Empty project");
            await page.GetByTestId("create-project-workspace").FillAsync(workspace);
            await Expect(page.Locator("#first-objective")).ToHaveCountAsync(0);
            await page.GetByTestId("create-project-next").ClickAsync();
            await page.GetByTestId("create-project-empty").ClickAsync();
            if (hasKit)
            {
                await Expect(page.GetByTestId("workspace-pipeline-confirmation")).ToContainTextAsync("main.kittyclaw-pipeline");
                Assert.Empty(await http.GetFromJsonAsync<JsonElement[]>("/api/projects") ?? []);
                await page.GetByTestId(acceptImport ? "workspace-pipeline-accept" : "workspace-pipeline-decline").ClickAsync();
            }
            if (acceptImport)
            {
                var review = page.Locator(".pipeline-kit-dialog");
                var powershell = review.Locator(".pipeline-kit-approval").Filter(new() { HasText = "executePowerShell" }).GetByRole(AriaRole.Checkbox);
                var network = review.Locator(".pipeline-kit-approval").Filter(new() { HasText = "httpRequest" }).GetByRole(AriaRole.Checkbox);
                var python = review.Locator(".pipeline-kit-approval").Filter(new() { HasText = "executePython" }).GetByRole(AriaRole.Checkbox);
                await Expect(powershell).Not.ToBeCheckedAsync();
                await Expect(network).Not.ToBeCheckedAsync();
                await Expect(python).Not.ToBeCheckedAsync();
                await Expect(review).ToContainTextAsync("Python ingestion");
                Assert.Single(await http.GetFromJsonAsync<JsonElement[]>("/api/projects/empty-project/pipelines") ?? []);
                if (approveActions) { await powershell.CheckAsync(); await network.CheckAsync(); await python.CheckAsync(); }
                await review.GetByTestId("confirm-pipeline-import").ClickAsync();
                await Expect(review).ToContainTextAsync("Pipeline imported");
                if (approveActions) await Expect(review).Not.ToContainTextAsync("approval:executePowerShell");
                else await Expect(review).ToContainTextAsync("approval:executePowerShell");
                if (approveActions) await Expect(review).Not.ToContainTextAsync("approval:executePython");
                else await Expect(review).ToContainTextAsync("approval:executePython");
                await review.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).Last.ClickAsync();
            }
            await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/board/empty-project$"));
            await Expect(page.Locator(".first-ticket-draft")).ToHaveCountAsync(0);
            await Expect(page.Locator(".migration-wizard")).ToHaveCountAsync(0);
            var project = await http.GetFromJsonAsync<JsonElement>("/api/projects/empty-project");
            Assert.Equal(workspace, project.GetProperty("workspacePath").GetString());
            Assert.Empty(await http.GetFromJsonAsync<JsonElement[]>("/api/projects/empty-project/tickets") ?? []);
            var pipelines = await http.GetFromJsonAsync<JsonElement[]>("/api/projects/empty-project/pipelines");
            Assert.Equal(acceptImport ? 2 : 1, pipelines!.Length);
            var main = Assert.Single(pipelines, p => p.GetProperty("isDefault").GetBoolean());
            Assert.Equal("main", main.GetProperty("slug").GetString());
            Assert.True(main.GetProperty("isDefault").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(workspace, ".git")));
            Assert.False(File.Exists(Path.Combine(workspace, "AGENTS.md")));
            Assert.False(File.Exists(Path.Combine(workspace, "CLAUDE.md")));
            if (hasKit) Assert.True(File.Exists(kitPath));
        }
        finally
        {
            if (hasKit && File.Exists(kitPath)) File.Delete(kitPath);
            // The empty-project flow must never populate the selected workspace.
            if (Directory.Exists(workspace) && !Directory.EnumerateFileSystemEntries(workspace).Any())
                Directory.Delete(workspace);
        }
    }

    private static string WebExecutable()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx"))) continue;
            var name = OperatingSystem.IsWindows() ? "KittyClaw.Web.exe" : "KittyClaw.Web";
            return Path.Combine(directory.FullName, "KittyClaw.Web", "bin", configuration, "net10.0", name);
        }
        throw new DirectoryNotFoundException("Could not locate the KittyClaw repository root.");
    }
}
