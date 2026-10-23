using System.Net;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Tests.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class ColumnMemoryHookJourneyTests
{
    [Fact]
    public async Task Isolated_server_routes_past_Git_hook_failure_resumes_Todo_and_retries_only_memory_after_restart()
    {
        using var temp = new TempDir();
        var repo = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "main");
        var data = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), "{\"OnboardingSeen\":true,\"Language\":\"en\"}");
        var projects = new ProjectService(data);
        var project = await projects.CreateProjectAsync("Isolated memory hook journey");
        await projects.UpdateProjectAsync(project.Slug, repo, worktreesEnabled: false);
        await projects.TogglePauseAsync(project.Slug);
        var columns = new ColumnService(projects);
        var tickets = new TicketService(projects, new MemberService(projects));
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var todo = await columns.CreateColumnAsync(project.Slug, "Todo");
        var worker = await columns.CreateColumnAsync(project.Slug, "Worker A");
        var principal = await columns.CreateColumnAsync(project.Slug, "Principal");
        await processors.SaveAsync(project.Slug, worker.Id, "Worker", "Deliver the artifact.", "claude-sonnet-4-6",
            true, 1, [], [], [], maxAttempts: 1, defaultTargetColumnId: principal.Id);
        await processors.SaveAsync(project.Slug, todo.Id, "Qualifier", "Continue from existing delivered work and user approval.", "claude-sonnet-4-6",
            true, 1, [], [], [], maxAttempts: 1, defaultTargetColumnId: principal.Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "ShinyDeal 45 reproduction", status: worker.Name, columnId: worker.Id);
        // This hook lives only in the disposable repository. The flag models an external reservation.
        var hook = Path.Combine(repo, ".git", "hooks", "pre-commit");
        var flag = Path.Combine(repo, ".git", "memory-blocked");
        await File.WriteAllTextAsync(flag, "reserved");
        await File.WriteAllTextAsync(hook,
            "#!/bin/sh\nif test -f \"$(git rev-parse --git-dir)/memory-blocked\"; then echo 'Integration reservation: memory commit refused' >&2; exit 1; fi\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var scenario = Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO");
        var scenarioDir = Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIOS_DIR");
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "integration-memory.ndjson"), [
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            JsonSerializer.Serialize(new { type = "assistant", message = new { role = "assistant", content = new[] {
                new { type = "text", text = JsonSerializer.Serialize(new { outcome = "integration_ready", skillsUsed = Array.Empty<string>(),
                    summary = "Delivered artifact; existing integration reservation preserved.",
                    lessons = new[] { "Preserve the completed delivery and retry knowledge persistence separately." } }) } } } }),
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1,\"total_cost_usd\":0}",
            "{\"_meta\":{\"exit\":0}}"
        ]);
        Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO", "integration-memory");
        Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIOS_DIR", temp.Path);
        string executionId;
        try
        {
            using (var factory = new Factory(data))
            {
                factory.UseKestrel(0);
                using var http = factory.CreateClient();
                var liveProjects = factory.Services.GetRequiredService<ProjectService>();
                await liveProjects.TogglePauseAsync(project.Slug);
                var engine = factory.Services.GetRequiredService<ColumnProcessingEngine>();
                engine.Signal(project.Slug);
                var executions = factory.Services.GetRequiredService<ColumnExecutionService>();
                await WaitFor(async () => (await executions.ListAsync(project.Slug, ticket.Id)).Any(e =>
                    e.Status == ColumnExecutionStatus.Completed && e.CapitalizationStatus == MemoryCapitalizationStatus.RetryRequired));
                var delivered = Assert.Single(await executions.ListAsync(project.Slug, ticket.Id));
                executionId = delivered.Id;
                Assert.Equal("integration_ready", delivered.Outcome);
                Assert.Contains("Integration reservation", delivered.CapitalizationError);
                Assert.Equal(1, delivered.Attempt);
                var liveTickets = factory.Services.GetRequiredService<TicketService>();
                Assert.Equal(principal.Id, (await liveTickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
                var html = await http.GetStringAsync($"/board/{project.Slug}");
                Assert.Contains("Retry memory only", html);
                if (Environment.GetEnvironmentVariable("KITTYCLAW_JEV_DEBUG_E2E") == "1")
                {
                    using var playwright = await Playwright.CreateAsync();
                    await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
                    var page = await browser.NewPageAsync();
                    await page.GotoAsync(new Uri(http.BaseAddress!, $"/board/{project.Slug}").ToString());
                    await Microsoft.Playwright.Assertions.Expect(page.Locator($".ticket-card[data-ticket-id='{ticket.Id}'] .column-execution-recovery")).ToContainTextAsync("Retry memory only");
                }
                await liveTickets.AddCommentAsync(project.Slug, ticket.Id, "I approve. Continue the existing integration.", "owner");
                await liveTickets.MoveTicketAsync(project.Slug, ticket.Id, todo.Name);
                engine.Signal(project.Slug);
                await WaitFor(async () => (await executions.ListAsync(project.Slug, ticket.Id)).Count(e => e.Status == ColumnExecutionStatus.Completed) == 2);
                var history = await executions.ListAsync(project.Slug, ticket.Id);
                Assert.Equal(2, history.Count);
                Assert.Equal("owner-feedback", history.Single(e => e.Id != executionId).TriggerSignalType);
                Assert.Equal(principal.Id, (await liveTickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
                Assert.Equal(2, factory.Services.GetRequiredService<AgentRunRegistry>().AllForTicket(project.Slug, ticket.Id).Count());
                Assert.True(File.Exists(flag));
            }
            // A new web host uses the same isolated durable data. No real project is registered.
            using (var restarted = new Factory(data))
            {
                restarted.UseKestrel(0);
                using var http = restarted.CreateClient();
                var executions = restarted.Services.GetRequiredService<ColumnExecutionService>();
                var before = await executions.ListAsync(project.Slug, ticket.Id);
                Assert.Equal(2, before.Count);
                Assert.All(before, e => Assert.Equal(MemoryCapitalizationStatus.RetryRequired, e.CapitalizationStatus));
                // Simulate the external reservation owner allowing commits again; keep the hook installed.
                File.Delete(flag);
                var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                    http.PostAsync($"/api/projects/{project.Slug}/column-executions/{executionId}/retry-memory", null)));
                Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
                Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.NotFound) == 1,
                    string.Join("; ", await Task.WhenAll(responses.Select(async r => $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}"))));
                var after = await executions.ListAsync(project.Slug, ticket.Id);
                Assert.Equal(2, after.Count);
                Assert.Equal(MemoryCapitalizationStatus.Succeeded, after.Single(e => e.Id == executionId).CapitalizationStatus);
                Assert.Equal("integration_ready", after.Single(e => e.Id == executionId).Outcome);
                Assert.True(File.Exists(hook));
                var canonical = Path.Combine(repo, ".agents", "processors", $"column-{worker.Id}", "memory", "MEMORY.md");
                Assert.Equal(1, (await File.ReadAllTextAsync(canonical)).Split("Preserve the completed delivery").Length - 1);
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(canonical)!, "pipeline-lessons.md")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO", scenario);
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIOS_DIR", scenarioDir);
        }
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(100); }
        Assert.Fail("The isolated server did not reach the expected durable state.");
    }

    private sealed class Factory(string data) : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("KITTYCLAW_DATA_DIR", data).UseSetting("KITTYCLAW_DESKTOP", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "data-protection"))));
        }
    }
}
