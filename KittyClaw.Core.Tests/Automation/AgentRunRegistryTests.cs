using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public class AgentRunRegistryTests
{
    [Fact]
    public void Complete_IsIdempotent_DoesNotDowngradeTerminalStatus()
    {
        var registry = new AgentRunRegistry();
        var run = new AgentRun
        {
            RunId = "r1", ProjectSlug = "p", TicketId = null,
            AgentName = "a", SkillFile = "a/SKILL.md",
            ConcurrencyGroup = "a", StartedAt = DateTime.UtcNow,
        };
        registry.Register(run);

        registry.Complete("r1", AgentRunStatus.Completed, 0);
        Assert.Equal(AgentRunStatus.Completed, run.Status);

        // Stray second call must not downgrade to Failed
        registry.Complete("r1", AgentRunStatus.Failed, -1);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void Constructor_ReconcilesStaleLRunningSnapshots_ToStopped()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);

        // Persist a run that looks like it was still Running when the process died
        var staleRun = new AgentRun
        {
            RunId = "stale", ProjectSlug = "p", TicketId = null,
            AgentName = "a", SkillFile = "a/SKILL.md",
            ConcurrencyGroup = "a", StartedAt = DateTime.UtcNow,
        };
        // Status is Running (default) — simulate orphaned run
        store.Save(staleRun);

        var registry = new AgentRunRegistry(store);
        var loaded = registry.Get("stale");

        Assert.NotNull(loaded);
        Assert.Equal(AgentRunStatus.Stopped, loaded!.Status);
        Assert.NotNull(loaded.EndedAt);
    }
}

[Collection("MockClaude")]
public class ClaudeRunnerPumpExceptionTests
{
    /// <summary>
    /// An OnEvent subscriber that throws must not leave the run in Running state.
    /// The runner must catch the exception from the pump and complete the run as Failed.
    /// </summary>
    [Fact]
    public async Task ThrowingEventSubscriber_RunEndsAsFailed_NotRunning()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("pump-throw-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "default");

        var runs = new AgentRunRegistry();
        var runner = new ClaudeRunner(new SessionRegistry(), runs, new RunConcurrencyGate(1),
            NullLogger<ClaudeRunner>.Instance);

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "test-agent/SKILL.md",
            MaxTurns = 1,
            OnEventHook = _ => throw new InvalidOperationException("subscriber intentionally throws"),
        };

        var run = await runner.RunAsync(ctx, CancellationToken.None);

        Assert.NotEqual(AgentRunStatus.Running, run.Status);
    }
}
