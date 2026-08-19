using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public class AgentRunRegistryTests
{
    [Fact]
    public void TicketQueries_ReturnOnlyIndexedRunsForTheRequestedProjectAndTicket()
    {
        var registry = new AgentRunRegistry();
        var expected = NewRun("expected", "project-a", 42);
        var completed = NewRun("completed", "project-a", 42);
        var otherTicket = NewRun("other-ticket", "project-a", 43);
        var otherProject = NewRun("other-project", "project-b", 42);
        registry.Register(expected);
        registry.Register(completed);
        registry.Register(otherTicket);
        registry.Register(otherProject);
        registry.Complete(completed.RunId, AgentRunStatus.Completed, 0);

        Assert.Equal([expected.RunId], registry.ActiveForTicket("project-a", 42).Select(r => r.RunId));
        Assert.Equal(
            [completed.RunId, expected.RunId],
            registry.AllForTicket("project-a", 42).Select(r => r.RunId).Order());
    }

    [Fact]
    public void TicketIndex_StaysConsistentWhenRunsAreReplacedRemovedAndPurged()
    {
        var registry = new AgentRunRegistry();
        registry.Register(NewRun("replace", "project-a", 1));
        registry.Register(NewRun("replace", "project-a", 2));
        var removed = NewRun("removed", "project-a", 2);
        registry.Register(removed);
        registry.Remove(removed.RunId);
        var expired = NewRun("expired", "project-a", 2);
        expired.Status = AgentRunStatus.Completed;
        expired.EndedAt = DateTime.UtcNow.AddDays(-2);
        registry.Register(expired);

        registry.PurgeOld(TimeSpan.FromDays(1));

        Assert.Empty(registry.AllForTicket("project-a", 1));
        Assert.Equal(["replace"], registry.AllForTicket("project-a", 2).Select(r => r.RunId));
    }

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
    public void Push_UpdatesLastActivityAt_ToEventTimestamp()
    {
        var run = new AgentRun
        {
            RunId = "r1", ProjectSlug = "p", TicketId = null,
            AgentName = "a", SkillFile = "a/SKILL.md",
            ConcurrencyGroup = "a", StartedAt = DateTime.UtcNow,
        };

        var t = DateTime.UtcNow.AddSeconds(5);
        run.Push(new StreamEvent(t, "assistant", "heartbeat"));

        Assert.Equal(t, run.LastActivityAt);
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

    [Fact]
    public void Constructor_ExposesPersistedRunningChatAsInterruptedForAutomaticRecovery()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        var firstRegistry = new AgentRunRegistry(store);
        var chatRun = new AgentRun
        {
            RunId = "chat-stale", ProjectSlug = "p", TicketId = 42,
            AgentName = "owner-chat", SkillFile = "chat",
            ConcurrencyGroup = "chat:p:owner-chat", StartedAt = DateTime.UtcNow,
            ChatTarget = "owner-chat", SessionId = "session-123",
        };

        firstRegistry.Register(chatRun);
        firstRegistry.Persist(chatRun);

        var restartedRegistry = new AgentRunRegistry(store);
        var interrupted = restartedRegistry.LastInterruptedForChatTarget("p", "owner-chat");

        Assert.NotNull(interrupted);
        Assert.Equal("chat-stale", interrupted!.RunId);
        Assert.Equal("owner-chat", interrupted.ChatTarget);
        Assert.Equal("session-123", interrupted.SessionId);
        Assert.Equal(AgentRunStatus.Stopped, interrupted.Status);
        Assert.Contains(interrupted.SnapshotBuffer(), e => e.Kind == "interrupted");
        Assert.Equal(["chat-stale"], restartedRegistry.InterruptedChats().Select(r => r.RunId));

        var recovered = new AgentRun
        {
            RunId = "chat-recovered", ProjectSlug = "p", TicketId = 42,
            AgentName = "owner-chat", SkillFile = "chat",
            ConcurrencyGroup = "chat:p:owner-chat", StartedAt = DateTime.UtcNow.AddSeconds(1),
            ChatTarget = "owner-chat", SessionId = "session-123",
        };
        restartedRegistry.Register(recovered);
        restartedRegistry.Complete(recovered.RunId, AgentRunStatus.Completed, 0);

        Assert.Null(restartedRegistry.LastInterruptedForChatTarget("p", "owner-chat"));
        Assert.Empty(restartedRegistry.InterruptedChats());
    }

    private static AgentRun NewRun(string runId, string projectSlug, int? ticketId) => new()
    {
        RunId = runId,
        ProjectSlug = projectSlug,
        TicketId = ticketId,
        AgentName = "agent",
        SkillFile = "agent/SKILL.md",
        ConcurrencyGroup = "agent",
        StartedAt = DateTime.UtcNow,
    };
}

[Collection("MockClaude")]
public class AgentRunnerPumpExceptionTests
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
        var runner = new AgentRunner(new SessionRegistry(), runs, new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance);

        var ctx = new AgentRunContext
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
