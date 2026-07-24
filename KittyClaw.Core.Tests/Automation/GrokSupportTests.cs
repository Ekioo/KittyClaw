using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

// In the MockClaude collection: these tests mutate the process-wide KITTYCLAW_GROK_BIN env var
// (restored per test) and GrokCli's cached resolution via ResetForTests, so they must not run
// in parallel with other automation tests.
[Collection("MockClaude")]
public class GrokSupportTests
{
    private static AgentRun NewRun() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        ProjectSlug = "grok-test",
        TicketId = null,
        AgentName = "agent",
        SkillFile = "agent/SKILL.md",
        ConcurrencyGroup = "agent",
        StartedAt = DateTime.UtcNow,
    };

    private static bool TryMap(string line, AgentRun run)
    {
        using var doc = JsonDocument.Parse(line);
        return GrokStreamAdapter.TryMap(doc.RootElement, line, run);
    }

    /// <summary>Points KITTYCLAW_GROK_BIN at an existing file (the test assembly) so GrokCli
    /// resolves as installed; restores the previous state on dispose.</summary>
    private sealed class FakeGrokInstall : IDisposable
    {
        private readonly string? _previous;
        public string BinPath { get; }

        public FakeGrokInstall(string? binPath = null)
        {
            _previous = Environment.GetEnvironmentVariable("KITTYCLAW_GROK_BIN");
            BinPath = binPath ?? typeof(GrokSupportTests).Assembly.Location;
            Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", BinPath);
            GrokCli.ResetForTests();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", _previous);
            GrokCli.ResetForTests();
        }
    }

    // ── Model routing ──────────────────────────────────────────────────────

    [Fact]
    public void Routing_ClaudeModel_IsClaudeProviderWithoutEnv()
    {
        var r = ModelRouting.Resolve("claude-sonnet-4-6", null);
        Assert.Equal(CliProvider.Claude, r.Provider);
        Assert.Null(r.ExtraEnv);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Routing_NullModel_IsClaudeProvider()
    {
        var r = ModelRouting.Resolve(null, "http://localhost:11434");
        Assert.Equal(CliProvider.Claude, r.Provider);
        Assert.Null(r.ExtraEnv);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Routing_OllamaModel_InjectsAnthropicEnv()
    {
        var r = ModelRouting.Resolve("qwen3-coder:30b", "http://localhost:11434/");
        Assert.Equal(CliProvider.Claude, r.Provider);
        Assert.Null(r.Error);
        Assert.NotNull(r.ExtraEnv);
        Assert.Equal("http://localhost:11434", r.ExtraEnv!["ANTHROPIC_BASE_URL"]);
        Assert.Equal("qwen3-coder:30b", r.ExtraEnv["ANTHROPIC_MODEL"]);
    }

    [Fact]
    public void Routing_OllamaModel_WithoutBaseUrl_IsError()
    {
        var r = ModelRouting.Resolve("qwen3-coder:30b", null);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Routing_GrokModel_WhenInstalled_IsGrokProvider()
    {
        using var grok = new FakeGrokInstall();
        var r = ModelRouting.Resolve("grok-4", null);
        Assert.Equal(CliProvider.Grok, r.Provider);
        Assert.Null(r.ExtraEnv);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Routing_GrokModel_WhenNotInstalled_IsError()
    {
        var previous = Environment.GetEnvironmentVariable("KITTYCLAW_GROK_BIN");
        Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", null);
        GrokCli.ResetForTests();
        try
        {
            // Soft-skip when the host genuinely has grok on PATH.
            if (GrokCli.IsInstalled) return;
            var r = ModelRouting.Resolve("grok-4", "http://localhost:11434");
            Assert.Equal(CliProvider.Claude, r.Provider);
            Assert.NotNull(r.Error);
            Assert.Contains("grok", r.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(r.ExtraEnv); // never routed to Ollama
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", previous);
            GrokCli.ResetForTests();
        }
    }

    [Fact]
    public void WithFallback_SwapsModelProviderAndEnv_AndClearsFallback()
    {
        var ctx = new AgentRunContext
        {
            ProjectSlug = "p",
            WorkspacePath = "w",
            AgentName = "a",
            SkillFile = "a/SKILL.md",
            Model = "qwen3-coder:30b",
            Provider = CliProvider.Claude,
            Env = new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "http://localhost:11434" },
            FallbackModel = "grok-4.5",
            FallbackProvider = CliProvider.Grok,
            FallbackEnv = new Dictionary<string, string>(),
        };

        var fb = ctx.WithFallback();

        Assert.Equal("grok-4.5", fb.Model);
        Assert.Equal(CliProvider.Grok, fb.Provider);
        // The primary's Ollama env overrides must not leak into the fallback subprocess.
        Assert.Empty(fb.Env);
        // The retry must not chain into another fallback.
        Assert.Null(fb.FallbackModel);
    }

    // ── GrokCli helpers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("grok-4", true)]
    [InlineData("grok-build-0.1", true)]
    [InlineData("GROK-4", true)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("qwen3-coder:30b", false)]
    [InlineData(null, false)]
    public void IsGrokModel_MatchesPrefix(string? model, bool expected) =>
        Assert.Equal(expected, GrokCli.IsGrokModel(model));

    [Fact]
    public void ParseModelList_ExtractsIdsFromTableOutput()
    {
        const string stdout = """
            Available models:

            NAME              CONTEXT   DEFAULT
            grok-build-0.1    256k      *
            grok-4            256k
            grok-4-fast       128k
            some-other-row    n/a
            """;
        var models = GrokCli.ParseModelList(stdout);
        Assert.Equal(new[] { "grok-build-0.1", "grok-4", "grok-4-fast" }, models);
    }

    [Fact]
    public void ParseModelList_EmptyOrGarbage_YieldsNothing()
    {
        Assert.Empty(GrokCli.ParseModelList(""));
        Assert.Empty(GrokCli.ParseModelList("error: not logged in\nrun `grok login` first"));
    }

    // ── Stream adapter ──────────────────────────────────────────────────────

    [Fact]
    public void Adapter_TextEvent_BecomesAssistantEvent()
    {
        var run = NewRun();
        Assert.True(TryMap("""{"type":"text","text":"Hello from grok"}""", run));
        var ev = Assert.Single(run.SnapshotBuffer());
        Assert.Equal("assistant", ev.Kind);
        Assert.Equal("[assistant] Hello from grok", ev.Text);
    }

    [Fact]
    public void Adapter_ToolUseEvent_BecomesToolUseEvent()
    {
        var run = NewRun();
        Assert.True(TryMap("""{"type":"tool_use","name":"bash","input":{"command":"ls"}}""", run));
        var ev = Assert.Single(run.SnapshotBuffer());
        Assert.Equal("tool_use", ev.Kind);
        Assert.Equal("bash", ev.Text);
        Assert.Contains("ls", ev.Detail);
    }

    [Fact]
    public void Adapter_FinalEvent_RecordsUsageAndPushesResult()
    {
        var run = NewRun();
        Assert.True(TryMap(
            """{"type":"result","text":"done","stopReason":"stop","usage":{"input_tokens":100,"output_tokens":40},"cost":0.012}""",
            run));
        var ev = Assert.Single(run.SnapshotBuffer());
        Assert.Equal("result", ev.Kind);
        Assert.Equal(100, run.InputTokens);
        Assert.Equal(40, run.OutputTokens);
        Assert.Equal(0.012m, run.TotalCostUsd);
    }

    [Fact]
    public void Adapter_UsageInCamelCase_IsAccepted()
    {
        var run = NewRun();
        Assert.True(TryMap("""{"stopReason":"stop","usage":{"inputTokens":7,"outputTokens":3}}""", run));
        Assert.Equal(7, run.InputTokens);
        Assert.Equal(3, run.OutputTokens);
    }

    [Fact]
    public void Adapter_StepFinishWithUsage_AccumulatesWithoutResultEvent()
    {
        var run = NewRun();
        Assert.True(TryMap("""{"type":"step_finish","usage":{"input_tokens":10,"output_tokens":5}}""", run));
        Assert.Equal(10, run.InputTokens);
        Assert.Empty(run.SnapshotBuffer()); // no premature terminal event
    }

    [Fact]
    public void Adapter_UnknownOrClaudeShapedEvents_FallThrough()
    {
        var run = NewRun();
        // grok event type we don't know
        Assert.False(TryMap("""{"type":"step_start","step":1}""", run));
        // claude-style assistant envelope (from a mock or a mis-routed stream)
        Assert.False(TryMap(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""", run));
        Assert.Empty(run.SnapshotBuffer());
    }

    [Fact]
    public void Adapter_ErrorEvent_BecomesErrorEvent()
    {
        var run = NewRun();
        Assert.True(TryMap("""{"type":"error","message":"boom"}""", run));
        var ev = Assert.Single(run.SnapshotBuffer());
        Assert.Equal("error", ev.Kind);
        Assert.Equal("boom", ev.Text);
    }

    // ── Dispatch integration ────────────────────────────────────────────────

    private static async Task<(ActionExecutor executor, ProjectRuntime rt, AgentRunRegistry runs)>
        BuildExecutorAsync(string dataDir, string projectSlug)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync(projectSlug);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "grok-agent", scenario: "default");

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 4);
        var runner = new AgentRunner(sessions, runs, gate, NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var appSettings = new AppSettingsService(dataDir);
        var loc = new LocalizationService(appSettings);
        var tickets = new TicketService(projects, members);
        var runState = new RunStateManager(runs, cost, tickets, NullLogger.Instance);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState,
            NullLogger.Instance);

        var rt = new ProjectRuntime(project.Slug) { Workspace = workspace, Config = new AutomationConfig { Automations = [] } };
        return (executor, rt, runs);
    }

    private static AutomationRule MakeAutomation(string model) => new()
    {
        Id = "test-grok",
        Enabled = true,
        Trigger = new StatusChangeTriggerSpec { From = "Todo", To = "Done", PollSeconds = 30 },
        Conditions = [],
        Actions = [new RunAgentActionSpec { Agent = "grok-agent", MaxTurns = 1, Model = model }],
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

    [Fact]
    public async Task Dispatch_GrokModel_WithoutCli_EmitsErrorWithoutLaunch()
    {
        var previous = Environment.GetEnvironmentVariable("KITTYCLAW_GROK_BIN");
        Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", null);
        GrokCli.ResetForTests();
        try
        {
            if (GrokCli.IsInstalled) return; // host has a real grok — nothing to assert

            using var tmp = new TempDir();
            var (executor, rt, runs) = await BuildExecutorAsync(tmp.Path, "grok-missing-cli-test");
            await executor.ExecuteAutomationAsync(rt, MakeAutomation("grok-4"), new TriggerFiring(null, null, "Done"), CancellationToken.None);

            await Task.Delay(500);
            var run = runs.AllForProject(rt.Slug).FirstOrDefault();
            Assert.NotNull(run);
            var events = run.SnapshotBuffer();
            Assert.Contains(events, e => e.Kind == "error" && e.Text.Contains("grok", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(events, e => e.Kind == "launch");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", previous);
            GrokCli.ResetForTests();
        }
    }

    [Fact]
    public async Task Dispatch_GrokModel_WithMockBinary_RunsSubprocess()
    {
        // Reuse the claude mock as a stand-in grok binary: it tolerates grok's flags, ignores
        // stdin being empty, and replays the default NDJSON scenario.
        var mockBin = Environment.GetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN");
        Assert.False(string.IsNullOrEmpty(mockBin), "MockClaude fixture did not resolve the mock binary");
        using var grok = new FakeGrokInstall(mockBin);

        using var tmp = new TempDir();
        var (executor, rt, runs) = await BuildExecutorAsync(tmp.Path, "grok-dispatch-test");
        await executor.ExecuteAutomationAsync(rt, MakeAutomation("grok-4"), new TriggerFiring(null, null, "Done"), CancellationToken.None);

        var run = await WaitForRunEndAsync(runs, rt.Slug, TimeSpan.FromSeconds(15));
        Assert.NotNull(run);
        Assert.Equal("grok-4", run.Model);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "launch");
    }
}
