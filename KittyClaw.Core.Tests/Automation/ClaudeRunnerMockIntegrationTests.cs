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
}
