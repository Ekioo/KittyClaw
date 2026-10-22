using System.Diagnostics;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class RunAgentDurableWorktreeIntegrationTests : IDisposable
{
    private readonly List<Fixture> _fixtures = [];

    [Fact]
    public async Task TicketlessAgent_CommitsDeclaredOutput_AndQueuesIntegration()
    {
        var fixture = await CreateFixtureAsync("durable-success",
            """
            {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
            {"_meta":{"write_file":{"path":"generated/result.txt","content":"durable"}}}
            {"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":1}
            """);
        var before = Fingerprint(fixture.Repository, fixture.Sentinel);

        await fixture.Executor.ExecuteAutomationAsync(fixture.Runtime,
            Automation(fixture.ScenarioDirectory, ["generated"]),
            new TriggerFiring(null, null, null), CancellationToken.None);
        var run = await WaitForFinishedRunAsync(fixture.Runs, fixture.Slug);
        var request = await WaitForMergeRequestAsync(fixture.Queue, fixture.Slug,
            WorktreeMergeStatus.Pending);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.NotEqual(fixture.Repository, run.WorkingDirectory);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "worktree"
            && e.Text.Contains("durable automation worktree", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, Fingerprint(fixture.Repository, fixture.Sentinel));
        Assert.Equal("durable", (await File.ReadAllTextAsync(
            Path.Combine(request.WorktreePath, "generated", "result.txt"))).Trim());

        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        // Integration only advances the durable tip; the local checkout catches up on sync.
        Assert.Equal(LocalCheckoutSyncStatus.Completed,
            (await fixture.Queue.SynchronizeNextAsync(fixture.Slug, CancellationToken.None))!.SyncStatus);
        Assert.Equal("durable", (await File.ReadAllTextAsync(
            Path.Combine(fixture.Repository, "generated", "result.txt"))).Trim());
        Assert.Empty(Git(request.WorktreePath, "status", "--porcelain=v1"));
        Assert.Null(await fixture.Queue.GetAlertSummaryAsync(fixture.Slug));
    }

    [Fact]
    public async Task TicketlessAgent_FailurePreservesIsolatedOutputForReview()
    {
        var fixture = await CreateFixtureAsync("durable-failure",
            """
            {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
            {"_meta":{"write_file":{"path":"generated/preserved.txt","content":"inspect"}}}
            {"type":"result","subtype":"error_during_execution","is_error":true,"duration_ms":1,"num_turns":1}
            {"_meta":{"exit":1}}
            """);
        var before = Fingerprint(fixture.Repository, fixture.Sentinel);

        await fixture.Executor.ExecuteAutomationAsync(fixture.Runtime,
            Automation(fixture.ScenarioDirectory, ["generated"]),
            new TriggerFiring(null, null, null), CancellationToken.None);
        var run = await WaitForFinishedRunAsync(fixture.Runs, fixture.Slug);
        var request = await WaitForMergeRequestAsync(fixture.Queue, fixture.Slug,
            WorktreeMergeStatus.NeedsReview);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(before, Fingerprint(fixture.Repository, fixture.Sentinel));
        Assert.True(File.Exists(Path.Combine(request.WorktreePath, "generated", "preserved.txt")));
        Assert.Contains("ended with status Failed", request.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TicketlessAgent_UndeclaredOutputFailsClosedAndKeepsPrimaryClean()
    {
        var fixture = await CreateFixtureAsync("durable-unexpected",
            """
            {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
            {"_meta":{"write_file":{"path":"outside/result.txt","content":"review"}}}
            {"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":1}
            """);
        var before = Fingerprint(fixture.Repository, fixture.Sentinel);

        await fixture.Executor.ExecuteAutomationAsync(fixture.Runtime,
            Automation(fixture.ScenarioDirectory, ["generated"]),
            new TriggerFiring(null, null, null), CancellationToken.None);
        var run = await WaitForFinishedRunAsync(fixture.Runs, fixture.Slug);
        var request = await WaitForMergeRequestAsync(fixture.Queue, fixture.Slug,
            WorktreeMergeStatus.NeedsReview);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(before, Fingerprint(fixture.Repository, fixture.Sentinel));
        Assert.Contains("outside/result.txt", request.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error"
            && e.Text.Contains("undeclared paths", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TicketlessPowerShellFollowedByAgent_ReleasesMaintenanceRouteBeforeAgentStarts()
    {
        var fixture = await CreateFixtureAsync("after-powershell",
            """
            {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
            {"_meta":{"write_file":{"path":"generated/agent.txt","content":"agent"}}}
            {"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":1}
            """);
        var automation = Automation(fixture.ScenarioDirectory, ["generated"]);
        automation.Actions.Insert(0, new ExecutePowerShellActionSpec
        {
            Script = "New-Item -ItemType Directory -Force generated | Out-Null; Set-Content generated/powershell.txt powershell",
            TimeoutSeconds = 30,
            VersionedWritePaths = ["generated"],
        });

        await fixture.Executor.ExecuteAutomationAsync(fixture.Runtime, automation,
            new TriggerFiring(null, null, null), CancellationToken.None);
        var run = await WaitForFinishedRunAsync(fixture.Runs, fixture.Slug);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal("powershell", (await File.ReadAllTextAsync(
            Path.Combine(run.WorkingDirectory!, "generated", "powershell.txt"))).Trim());
        Assert.Equal("agent", (await File.ReadAllTextAsync(
            Path.Combine(run.WorkingDirectory!, "generated", "agent.txt"))).Trim());
        Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
    }

    private async Task<Fixture> CreateFixtureAsync(string scenario, string scenarioContent)
    {
        var root = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(root.Path, "integration");
        var sentinel = Path.Combine(repository, "sentinel.txt");
        File.WriteAllText(sentinel, "primary");
        var scenarioDirectory = Path.Combine(root.Path, "scenarios");
        Directory.CreateDirectory(scenarioDirectory);
        await File.WriteAllTextAsync(Path.Combine(scenarioDirectory, scenario + ".ndjson"), scenarioContent);
        TestSkillBuilder.Create(repository, "durable-agent", scenario: scenario);
        Git(repository, "add", "sentinel.txt", ".agents/durable-agent/SKILL.md");
        Git(repository, "commit", "-m", "test: add durable agent fixture");

        var projects = new ProjectService(Path.Combine(root.Path, "data"));
        var project = await projects.CreateProjectAsync("agent durable lifecycle");
        await projects.UpdateProjectAsync(project.Slug, repository);
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true,
            integrationBranch: "integration");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var runs = new AgentRunRegistry();
        // Match the production registration: runtime channel/session state belongs under
        // KittyClaw's data directory, never below the project's primary checkout.
        var sessions = new SessionRegistry(root.Path);
        var cost = new CostTracker();
        var worktrees = new TicketWorktreeService(projects, tickets);
        var queue = new WorktreeMergeQueueService(projects, worktrees);
        var router = new DurableWriteRouter(projects, worktrees, queue);
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4),
            NullLogger<AgentRunner>.Instance);
        var executor = new ActionExecutor(tickets, members, new LabelService(projects), sessions, runs,
            runner, cost, new LocalizationService(new AppSettingsService(root.Path)), projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance), NullLogger.Instance, null, router);
        var fixture = new Fixture(root, repository, sentinel, scenarioDirectory, project.Slug, runs,
            queue, executor, new ProjectRuntime(project.Slug)
            {
                Workspace = repository,
                Config = new AutomationConfig(),
            });
        _fixtures.Add(fixture);
        return fixture;
    }

    private static AutomationRule Automation(string scenarioDirectory, List<string> paths) => new()
    {
        Id = "ticketless-agent",
        Enabled = true,
        Trigger = new IntervalTriggerSpec { Cron = "0 * * * *" },
        Actions = [new RunAgentActionSpec
        {
            Agent = "durable-agent",
            MaxTurns = 2,
            VersionedWritePaths = paths,
            Env = new() { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarioDirectory },
        }],
    };

    private static async Task<AgentRun> WaitForFinishedRunAsync(AgentRunRegistry runs, string slug)
    {
        for (var i = 0; i < 400; i++)
        {
            var run = runs.AllForProject(slug).SingleOrDefault(r => r.AgentName == "durable-agent");
            if (run is not null && run.Status != AgentRunStatus.Running) return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("The ticketless agent did not finish.");
    }

    private static async Task<WorktreeMergeRequest> WaitForMergeRequestAsync(
        WorktreeMergeQueueService queue, string slug, WorktreeMergeStatus expected)
    {
        for (var i = 0; i < 300; i++)
        {
            var request = (await queue.ListAsync(slug)).SingleOrDefault();
            if (request?.Status == expected) return request;
            await Task.Delay(50);
        }
        var current = (await queue.ListAsync(slug)).SingleOrDefault();
        throw new TimeoutException($"Merge request did not reach {expected}; current={current?.Status}.");
    }

    private static string Fingerprint(string repository, string sentinel) =>
        $"{Git(repository, "rev-parse", "HEAD").Trim()}\n{Git(repository, "status", "--porcelain=v1")}{File.ReadAllText(sentinel)}";

    private static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        foreach (var fixture in _fixtures) fixture.Dispose();
    }

    private sealed record Fixture(
        TempDir Root,
        string Repository,
        string Sentinel,
        string ScenarioDirectory,
        string Slug,
        AgentRunRegistry Runs,
        WorktreeMergeQueueService Queue,
        ActionExecutor Executor,
        ProjectRuntime Runtime) : IDisposable
    {
        public void Dispose() => Root.Dispose();
    }
}
