using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

// Finds KittyClaw.ClaudeMock/bin/**/claude(.exe) by walking up from the test assembly and sets
// KITTYCLAW_CLAUDE_BIN before any ClaudeRunner is constructed, because ResolveClaudeBinary is a
// static Lazy that caches on first access — the env var must be in place before that happens.
[CollectionDefinition("MockClaude")]
public sealed class MockClaudeCollection : ICollectionFixture<MockClaudeBinFixture> { }

public sealed class MockClaudeBinFixture : IDisposable
{
    public MockClaudeBinFixture()
    {
        var exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var mockBin = Path.Combine(dir.FullName, "KittyClaw.ClaudeMock", "bin");
            if (!Directory.Exists(mockBin)) continue;
            var found = Directory.EnumerateFiles(mockBin, exe, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                Environment.SetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN", found);
                return;
            }
        }
    }

    public void Dispose() => Environment.SetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN", null);
}

[Collection("MockClaude")]
public class ClaudeRunnerMockIntegrationTests
{
    [Fact]
    public async Task DispatchedAgent_ReceivesStreamEventsFromMock()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("integration-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "default");

        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 1);
        var runner = new ClaudeRunner(sessions, runs, gate, NullLogger<ClaudeRunner>.Instance);

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "test-agent/SKILL.md",
            MaxTurns = 1,
        };

        var run = await runner.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "assistant");
    }

    [Fact]
    public async Task ChatSession_CompletesSuccessfully_WithInlineSkill()
    {
        // Regression: chat sessions must NOT pass --remote-control to the claude CLI.
        // When an automation and a chat session share the same workspace, --remote-control
        // creates IPC files (payload.json) in the CWD that the chat process would pick up
        // instead of reading its own prompt from stdin.
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("chat-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var runner = new ClaudeRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "You are a test agent. <!--scenario:default-->",
            ExtraContext = "Hello",
            MaxTurns = 1,
            SessionScope = "chat",
            ConcurrencyGroup = $"chat:{project.Slug}:test-agent",
            RetryOnResumeFailure = true,
        };

        var run = await runner.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task ScenarioWithErrorExit_MarksRunAsFailed()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("error-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "error-exit");

        var runner = new ClaudeRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "test-agent/SKILL.md",
            MaxTurns = 1,
        };

        var run = await runner.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(1, run.ExitCode);
    }

    // Regression for ticket #221: claude (Node) can emit its terminal `result` event yet never
    // exit, because the agent left a child process alive (e.g. qa-tester backgrounding an isolated
    // test server) that keeps the runtime from terminating. WaitForExitAsync would then block
    // forever and the run would stay Running indefinitely (the spinner that never stops). The
    // ResultExitGrace watchdog must force-kill the tree shortly after the result and complete the
    // run instead of hanging.
    [Fact]
    public async Task ResultEmittedButProcessNeverExits_CompletesViaWatchdog()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("hang-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        // Scenario: emit a success result, then "hang" via a 10-minute delay so the mock process
        // stays alive long past its result (standing in for a backgrounded child keeping it alive).
        var scenarioDir = Path.Combine(tmp.Path, "mock-scenarios");
        Directory.CreateDirectory(scenarioDir);
        await File.WriteAllTextAsync(Path.Combine(scenarioDir, "result-then-hang.ndjson"), string.Join('\n', new[]
        {
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Work done. Posting verdict.\"}]}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":42,\"num_turns\":1}",
            "{\"_meta\":{\"delay_ms\":600000}}",
        }));

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "result-then-hang");

        var runner = new ClaudeRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance)
        {
            ResultExitGrace = TimeSpan.FromSeconds(1), // don't wait the full 15s in the test
        };

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "test-agent/SKILL.md",
            MaxTurns = 1,
            Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarioDir },
        };

        // The run must finish well within the mock's 10-minute hang; a 30s budget proves the
        // watchdog fired rather than the test merely waiting out the mock.
        var run = await runner.RunAsync(ctx, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "result");
    }
}
