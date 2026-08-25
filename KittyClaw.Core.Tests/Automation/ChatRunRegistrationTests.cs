using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

// Regression tests for ticket #347: POST /chat/start returned a run id before the run was
// registered, so the drawer's immediate registry lookup (and the SSE stream endpoint)
// reported "Run not found" while the worktree preparation was still running.
[Collection("MockClaude")]
public sealed class ChatRunRegistrationTests
{
    [Fact]
    public async Task ChatRun_IsQueryableInRegistryBeforeAsyncPreparationCompletes()
    {
        using var root = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(root.Path, "integration");
        var scenarioDir = Path.Combine(root.Path, "scenarios");
        Directory.CreateDirectory(scenarioDir);
        await File.WriteAllTextAsync(Path.Combine(scenarioDir, "chat-registration.ndjson"),
            """
            {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"hello"}]}}
            {"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":1}
            """);

        var projects = new ProjectService(Path.Combine(root.Path, "data"));
        var project = await projects.CreateProjectAsync("chat run registration");
        await projects.UpdateProjectAsync(project.Slug, repository);
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true,
            integrationBranch: "integration");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var runs = new AgentRunRegistry();
        var sessions = new SessionRegistry(root.Path);
        var worktrees = new TicketWorktreeService(projects, tickets);
        var router = new DurableWriteRouter(projects, worktrees);
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4),
            NullLogger<AgentRunner>.Instance, durableWrites: router, projects: projects);

        var runId = Guid.NewGuid().ToString("N");
        var ctx = new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = repository,
            AgentName = "owner-chat",
            SkillFile = "chat",
            InlineSkillContent = "# Chat\n\n<!--scenario:chat-registration-->\n",
            ExtraContext = "Hello",
            MaxTurns = 1,
            SessionScope = "chat",
            ChatTarget = "owner-chat",
            ConcurrencyGroup = $"chat:{project.Slug}:owner-chat",
            PresetRunId = runId,
            Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarioDir },
        };

        // Hold the maintenance-worktree gate so the run's interactive worktree resolution
        // genuinely blocks: in production the drawer queried the registry while this very
        // preparation was still in flight, which is exactly the reported race.
        var blockingRoute = await router.TryResolveWorkspaceAsync(project.Slug);
        Assert.NotNull(blockingRoute);
        try
        {
            // The chat/start endpoint fire-and-forgets RunAsync and immediately returns the
            // run id to the drawer. The run must therefore be resolvable through the registry
            // as soon as RunAsync yields — even though its preparation cannot complete yet.
            var runTask = runner.RunAsync(ctx, CancellationToken.None);
            Assert.NotNull(runs.Get(runId));

            // Release the gate and let the run finish normally through the mock CLI.
            await router.CommitAndQueueAsync(project.Slug, blockingRoute!, "test: release gate");
            var run = await runTask.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "assistant");
        }
        finally
        {
            // Idempotent lease release in case an assertion failed before the explicit one.
            await router.CommitAndQueueAsync(project.Slug, blockingRoute!, "test: release gate");
        }
    }

    [Fact]
    public async Task ChatRun_PreparationFailure_SurfacesErrorOnRegisteredRun()
    {
        using var root = new TempDir();
        var projects = new ProjectService(Path.Combine(root.Path, "data"));
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var runs = new AgentRunRegistry();
        var sessions = new SessionRegistry(root.Path);
        var worktrees = new TicketWorktreeService(projects, tickets);
        var router = new DurableWriteRouter(projects, worktrees);
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4),
            NullLogger<AgentRunner>.Instance, durableWrites: router, projects: projects);

        var runId = Guid.NewGuid().ToString("N");
        var ctx = new AgentRunContext
        {
            ProjectSlug = "ghost-project",
            WorkspacePath = root.Path,
            AgentName = "owner-chat",
            SkillFile = "chat",
            InlineSkillContent = "# Chat\n",
            ExtraContext = "Hello",
            MaxTurns = 1,
            SessionScope = "chat",
            ChatTarget = "owner-chat",
            ConcurrencyGroup = "chat:ghost-project:owner-chat",
            PresetRunId = runId,
        };

        // A failure during preparation (here: the interactive worktree cannot be resolved
        // because the project does not exist) must not vanish in the fire-and-forget caller:
        // the registered run fails with a visible error event the drawer can render.
        var runTask = runner.RunAsync(ctx, CancellationToken.None);
        Assert.NotNull(runs.Get(runId));

        var run = await runTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error"
            && e.Text.Contains("Interactive worktree resolution failed", StringComparison.Ordinal));
    }
}
