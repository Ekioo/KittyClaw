using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public class OllamaLocalModelTests
{
    private static async Task<(ActionExecutor executor, ProjectRuntime rt, AgentRunRegistry runs, Project project)>
        BuildAsync(string dataDir, string projectSlug)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync(projectSlug);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "local-agent", scenario: "default");

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 4);
        var runner = new ClaudeRunner(sessions, runs, gate, NullLogger<ClaudeRunner>.Instance);
        var cost = new CostTracker();
        var appSettings = new AppSettingsService(dataDir);
        var loc = new LocalizationService(appSettings);
        var tickets = new TicketService(projects, members);
        var runState = new RunStateManager(runs, cost, tickets, NullLogger.Instance);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState,
            NullLogger.Instance);

        var rt = new ProjectRuntime(project.Slug);
        rt.Workspace = workspace;
        rt.Config = new AutomationConfig { Automations = [] };

        return (executor, rt, runs, project);
    }

    private static AutomationRule MakeAutomation(string model) => new()
    {
        Id = "test-ollama",
        Enabled = true,
        Trigger = new StatusChangeTriggerSpec { From = "Todo", To = "Done", PollSeconds = 30 },
        Conditions = [],
        Actions = [new RunAgentActionSpec { Agent = "local-agent", MaxTurns = 1, Model = model }],
    };

    private static async Task<AgentRun?> WaitForRunEndAsync(AgentRunRegistry runs, string slug, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (!runs.ActiveForProject(slug).Any())
                return runs.AllForProject(slug).FirstOrDefault();
            await Task.Delay(50, cts.Token).ConfigureAwait(false);
        }
        return runs.AllForProject(slug).FirstOrDefault();
    }

    // Case 1: SaveLocalModelConfigAsync persists LocalModelBaseUrl and LocalModelName
    [Fact]
    public async Task SaveLocalModelConfig_PersistsAndReloads()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("ollama-save-test");

        await projects.SaveLocalModelConfigAsync(project.Slug, "http://localhost:11434", "qwen3-coder:30b");

        var loaded = await projects.GetProjectAsync(project.Slug);
        Assert.Equal("http://localhost:11434", loaded!.LocalModelBaseUrl);
        Assert.Equal("qwen3-coder:30b", loaded.LocalModelName);
    }

    // Case 2: Dispatch with openai-compatible uses LocalModelName as the effective model
    [Fact]
    public async Task Dispatch_OpenAiCompatible_SetsEffectiveModelToLocalModelName()
    {
        using var tmp = new TempDir();
        var (executor, rt, runs, project) = await BuildAsync(tmp.Path, "ollama-dispatch-test");
        var projects = new ProjectService(tmp.Path);
        await projects.SaveLocalModelConfigAsync(project.Slug, "http://localhost:11434", "qwen3-coder:30b");

        var automation = MakeAutomation("openai-compatible");
        var firing = new TriggerFiring(null, null, "Done");
        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None);

        var run = await WaitForRunEndAsync(runs, rt.Slug, TimeSpan.FromSeconds(15));
        Assert.NotNull(run);
        Assert.Equal("qwen3-coder:30b", run.Model);
    }

    // Case 3: Dispatch with an Anthropic model leaves the model name unchanged (non-regression)
    [Fact]
    public async Task Dispatch_AnthropicModel_ModelNameUnchanged()
    {
        using var tmp = new TempDir();
        var (executor, rt, runs, _) = await BuildAsync(tmp.Path, "anthropic-regression-test");

        var automation = MakeAutomation("claude-sonnet-4-6");
        var firing = new TriggerFiring(null, null, "Done");
        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None);

        var run = await WaitForRunEndAsync(runs, rt.Slug, TimeSpan.FromSeconds(15));
        Assert.NotNull(run);
        Assert.Equal("claude-sonnet-4-6", run.Model);
    }

    // Case 4: openai-compatible with empty LocalModelBaseUrl → error event, subprocess not launched
    [Fact]
    public async Task Dispatch_OpenAiCompatible_EmptyBaseUrl_EmitsErrorWithoutLaunch()
    {
        using var tmp = new TempDir();
        var (executor, rt, runs, project) = await BuildAsync(tmp.Path, "ollama-empty-url-test");
        await new ProjectService(tmp.Path).SaveLocalModelConfigAsync(project.Slug, "", "qwen3-coder:30b");

        var automation = MakeAutomation("openai-compatible");
        var firing = new TriggerFiring(null, null, "Done");
        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None);

        await Task.Delay(500);
        var run = runs.AllForProject(rt.Slug).FirstOrDefault();
        Assert.NotNull(run);
        var events = run.SnapshotBuffer();
        Assert.Contains(events, e => e.Kind == "error");
        Assert.DoesNotContain(events, e => e.Kind == "launch");
    }

    // Case 5: openai-compatible with empty LocalModelName → error event, subprocess not launched
    [Fact]
    public async Task Dispatch_OpenAiCompatible_EmptyModelName_EmitsErrorWithoutLaunch()
    {
        using var tmp = new TempDir();
        var (executor, rt, runs, project) = await BuildAsync(tmp.Path, "ollama-empty-name-test");
        await new ProjectService(tmp.Path).SaveLocalModelConfigAsync(project.Slug, "http://localhost:11434", "");

        var automation = MakeAutomation("openai-compatible");
        var firing = new TriggerFiring(null, null, "Done");
        await executor.ExecuteAutomationAsync(rt, automation, firing, CancellationToken.None);

        await Task.Delay(500);
        var run = runs.AllForProject(rt.Slug).FirstOrDefault();
        Assert.NotNull(run);
        var events = run.SnapshotBuffer();
        Assert.Contains(events, e => e.Kind == "error");
        Assert.DoesNotContain(events, e => e.Kind == "launch");
    }
}
