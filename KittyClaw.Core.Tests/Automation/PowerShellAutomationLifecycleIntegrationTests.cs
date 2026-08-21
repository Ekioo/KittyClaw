using System.Diagnostics;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Tests.Services;
using KittyClaw.Web.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class PowerShellAutomationLifecycleIntegrationTests : IDisposable
{
    private readonly List<Fixture> _fixtures = [];

    [Fact]
    public async Task TicketlessPowerShell_StopPreservesIsolatedWrite_AndLeavesPrimaryUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateFixtureAsync();
        var before = Fingerprint(fixture.Repository, fixture.Sentinel);
        var script = """
            New-Item -ItemType Directory -Force .agents\automation-proof | Out-Null
            Set-Content .agents\automation-proof\preserved.txt 'isolated'
            Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 3; Set-Content .agents/automation-proof/late.txt leaked'
            Start-Sleep -Seconds 30
            """;

        await fixture.Executor.ExecuteAutomationAsync(fixture.Runtime, Automation(script),
            new TriggerFiring(null, null, null), CancellationToken.None);
        var run = await WaitForRunAsync(fixture.Runs);
        var unrelated = fixture.Runs.Register(NewRun(fixture.Slug, "unrelated"));

        await AutomationEngine.CancelAndWaitForRunsAsync([run], CancellationToken.None);
        await AutomationEngine.CancelAndWaitForRunsAsync([run], CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(AgentRunStatus.Stopped, run.Status);
        Assert.Equal(AgentRunStatus.Running, unrelated.Status);
        Assert.Equal(before, Fingerprint(fixture.Repository, fixture.Sentinel));
        Assert.NotEqual(fixture.Repository, run.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(run.WorkingDirectory!, ".agents", "automation-proof", "preserved.txt")));
        Assert.False(File.Exists(Path.Combine(run.WorkingDirectory!, ".agents", "automation-proof", "late.txt")));
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "execution_workspace"
            && e.Text.Contains(run.WorkingDirectory!, StringComparison.OrdinalIgnoreCase));
        var request = Assert.Single(await fixture.Queue.ListAsync(fixture.Slug));
        Assert.Equal(WorktreeMergeStatus.NeedsReview, request.Status);
        fixture.Runs.Complete(unrelated.RunId, AgentRunStatus.Stopped, null);
    }

    [Fact]
    public async Task SuccessfulPowerShell_CommitsDeclaredPaths_AndQueuesTheirIntegration()
    {
        var fixture = await CreateFixtureAsync();
        var before = Fingerprint(fixture.Repository, fixture.Sentinel);
        var script = "New-Item -ItemType Directory -Force generated | Out-Null; Set-Content generated/result.txt durable";

        await fixture.Executor.ExecuteAutomationAsync(fixture.Runtime,
            Automation(script, ["generated"]), new TriggerFiring(null, null, null), CancellationToken.None);
        var run = await WaitForFinishedRunAsync(fixture.Runs);
        var request = await WaitForMergeRequestAsync(fixture.Queue, fixture.Slug,
            WorktreeMergeStatus.Pending);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(WorktreeMergeStatus.Pending, request.Status);
        Assert.Equal(before, Fingerprint(fixture.Repository, fixture.Sentinel));
        Assert.True(File.Exists(Path.Combine(request.WorktreePath, "generated", "result.txt")));

        var integrated = await fixture.Queue.ProcessNextAsync(fixture.Slug, CancellationToken.None);

        Assert.Equal(WorktreeMergeStatus.Completed, integrated!.Status);
        Assert.Equal("durable", (await File.ReadAllTextAsync(
            Path.Combine(fixture.Repository, "generated", "result.txt"))).Trim());
        Assert.True(Directory.Exists(request.WorktreePath));
        Assert.Empty(Git(request.WorktreePath, "status", "--porcelain"));
        Assert.Null(await fixture.Queue.GetAlertSummaryAsync(fixture.Slug));
    }

    [Fact]
    public async Task SuccessfulPowerShell_RebasesLegacyAbsoluteProjectPaths_ToMaintenanceWorktree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateFixtureAsync();
        var primaryOutput = Path.Combine(fixture.Repository, "generated", "absolute.txt");
        var primaryOutputDirectory = Path.GetDirectoryName(primaryOutput)!;
        var escapedOutput = primaryOutput.Replace("'", "''", StringComparison.Ordinal);
        var escapedOutputDirectory = primaryOutputDirectory.Replace("'", "''", StringComparison.Ordinal);
        var before = Fingerprint(fixture.Repository, fixture.Sentinel);
        var script = $"New-Item -ItemType Directory -Force '{escapedOutputDirectory}' | Out-Null; " +
            $"Set-Content '{escapedOutput}' isolated";

        await fixture.Executor.ExecuteAutomationAsync(fixture.Runtime,
            Automation(script, ["generated"]), new TriggerFiring(null, null, null), CancellationToken.None);
        var run = await WaitForFinishedRunAsync(fixture.Runs);
        var request = await WaitForMergeRequestAsync(fixture.Queue, fixture.Slug,
            WorktreeMergeStatus.Pending);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(before, Fingerprint(fixture.Repository, fixture.Sentinel));
        Assert.False(File.Exists(primaryOutput));
        Assert.Equal("isolated", (await File.ReadAllTextAsync(
            Path.Combine(request.WorktreePath, "generated", "absolute.txt"))).Trim());
    }

    [Fact]
    public async Task PauseAndServiceShutdown_CancelOnlyTheirSelectedActiveRuns_AndAreIdempotent()
    {
        var registry = new AgentRunRegistry();
        var firstProject = registry.Register(NewRun("first", "automation:first"));
        var secondProject = registry.Register(NewRun("second", "automation:second"));
        firstProject.Cancellation.Token.Register(() => registry.Complete(firstProject.RunId, AgentRunStatus.Stopped, null));
        secondProject.Cancellation.Token.Register(() => registry.Complete(secondProject.RunId, AgentRunStatus.Stopped, null));

        await AutomationEngine.CancelAndWaitForRunsAsync(registry.ActiveForProject("first"), CancellationToken.None);
        await AutomationEngine.CancelAndWaitForRunsAsync(registry.ActiveForProject("first"), CancellationToken.None);
        Assert.Equal(AgentRunStatus.Stopped, firstProject.Status);
        Assert.Equal(AgentRunStatus.Running, secondProject.Status);

        await AutomationEngine.CancelAndWaitForRunsAsync(registry.AllActive(), CancellationToken.None);
        await AutomationEngine.CancelAndWaitForRunsAsync(registry.AllActive(), CancellationToken.None);
        Assert.Equal(AgentRunStatus.Stopped, secondProject.Status);
    }

    [Fact]
    public async Task StopApi_WaitsForPowerShellDescendantCleanup_AndIsIdempotent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        var runs = api.Services.GetRequiredService<AgentRunRegistry>();
        var proof = Path.Combine(api.DataDir, "stop-api-late.txt");
        var unrelatedProof = Path.Combine(api.DataDir, "stop-api-unrelated-late.txt");
        var process = StartPowerShellRun(runs, "stop-api", proof);
        var unrelated = StartPowerShellRun(runs, "stop-api", unrelatedProof);

        var first = await client.PostAsync($"/api/projects/stop-api/runs/{process.Run.RunId}/stop", null);
        var second = await client.PostAsync($"/api/projects/stop-api/runs/{process.Run.RunId}/stop", null);
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(System.Net.HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(AgentRunStatus.Stopped, process.Run.Status);
        Assert.Equal(AgentRunStatus.Running, unrelated.Run.Status);
        Assert.False(File.Exists(proof));
        unrelated.Run.Cancellation.Cancel();
        await Task.WhenAll(process.Task, unrelated.Task);
    }

    [Fact]
    public async Task PauseApi_WaitsForSelectedPowerShellCleanup_AndLeavesOtherProjectRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        var projects = api.Services.GetRequiredService<ProjectService>();
        var project = await projects.CreateProjectAsync("pause target");
        var runs = api.Services.GetRequiredService<AgentRunRegistry>();
        var selectedProof = Path.Combine(api.DataDir, "pause-api-late.txt");
        var unrelatedProof = Path.Combine(api.DataDir, "unrelated-late.txt");
        var selected = StartPowerShellRun(runs, project.Slug, selectedProof);
        var unrelated = StartPowerShellRun(runs, "other-project", unrelatedProof);

        var first = await client.PostAsync($"/api/projects/{project.Slug}/pause", null);
        var second = await client.PostAsync($"/api/projects/{project.Slug}/pause", null);
        await Task.Delay(TimeSpan.FromSeconds(4));

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        Assert.Equal(AgentRunStatus.Stopped, selected.Run.Status);
        Assert.Equal(AgentRunStatus.Running, unrelated.Run.Status);
        Assert.False(File.Exists(selectedProof));
        unrelated.Run.Cancellation.Cancel();
        await Task.WhenAll(selected.Task, unrelated.Task);
    }

    [Fact]
    public async Task AutomationEngineStop_WaitsForEveryPowerShellDescendant_AndIsIdempotent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var api = new ApiFactory();
        _ = api.CreateClient();
        var runs = api.Services.GetRequiredService<AgentRunRegistry>();
        var proof = Path.Combine(api.DataDir, "engine-stop-late.txt");
        var secondProof = Path.Combine(api.DataDir, "engine-stop-second-late.txt");
        var process = StartPowerShellRun(runs, "engine-stop", proof);
        var second = StartPowerShellRun(runs, "engine-stop-other", secondProof);
        var engine = api.Services.GetRequiredService<AutomationEngine>();

        await engine.StopAsync(CancellationToken.None);
        await engine.StopAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(AgentRunStatus.Stopped, process.Run.Status);
        Assert.Equal(AgentRunStatus.Stopped, second.Run.Status);
        Assert.False(File.Exists(proof));
        Assert.False(File.Exists(secondProof));
        await Task.WhenAll(process.Task, second.Task);
    }

    [Fact]
    public void Restart_ReconcilesPersistedPowerShellRunOnce_WithoutRecreatingAProcess()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        var initial = new AgentRunRegistry(store);
        var run = initial.Register(NewRun("restart-project", "automation:nightly"));
        run.WorkingDirectory = Path.Combine(tmp.Path, "preserved-worktree");
        run.Push(new(DateTime.UtcNow, "execution_workspace", run.WorkingDirectory));
        initial.Persist(run);

        var restarted = new AgentRunRegistry(store);
        var recovered = Assert.Single(restarted.AllForProject("restart-project"));
        Assert.Equal(AgentRunStatus.Stopped, recovered.Status);
        Assert.Empty(restarted.AllActive());

        var restartedAgain = new AgentRunRegistry(store);
        var recoveredAgain = Assert.Single(restartedAgain.AllForProject("restart-project"));
        Assert.Equal(AgentRunStatus.Stopped, recoveredAgain.Status);
        Assert.Equal(recovered.EndedAt, recoveredAgain.EndedAt);
        Assert.Single(recoveredAgain.SnapshotBuffer(), e => e.Kind == "execution_workspace");
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var root = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(root.Path, "integration");
        var sentinel = Path.Combine(repository, "sentinel.txt");
        File.WriteAllText(sentinel, "primary");
        Git(repository, "add", "sentinel.txt");
        Git(repository, "commit", "-m", "test: add sentinel");
        var projects = new ProjectService(Path.Combine(root.Path, "data"));
        var project = await projects.CreateProjectAsync("powershell lifecycle");
        await projects.UpdateProjectAsync(project.Slug, repository);
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true, integrationBranch: "integration");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var runs = new AgentRunRegistry();
        var sessions = new SessionRegistry();
        var cost = new CostTracker();
        var worktrees = new TicketWorktreeService(projects, tickets);
        var queue = new WorktreeMergeQueueService(projects, worktrees);
        var router = new DurableWriteRouter(projects, worktrees, queue);
        var executor = new ActionExecutor(tickets, members, new LabelService(projects), sessions, runs,
            new AgentRunner(sessions, runs, new RunConcurrencyGate(4), NullLogger<AgentRunner>.Instance),
            cost, new LocalizationService(new AppSettingsService(root.Path)), projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance), NullLogger.Instance, null, router);
        var fixture = new Fixture(root, repository, sentinel, project.Slug, runs, queue, executor,
            new ProjectRuntime(project.Slug) { Workspace = repository, Config = new AutomationConfig() });
        _fixtures.Add(fixture);
        return fixture;
    }

    private static AutomationRule Automation(string script, List<string>? versionedWritePaths = null) => new()
    {
        Id = "nightly-write",
        Enabled = true,
        Trigger = new IntervalTriggerSpec { Cron = "0 * * * *" },
        Actions = [new ExecutePowerShellActionSpec
        {
            Script = script,
            TimeoutSeconds = 60,
            VersionedWritePaths = versionedWritePaths ?? [".agents", "tools", "scripts"],
        }],
    };

    private static AgentRun NewRun(string slug, string agent) => new()
    {
        RunId = Guid.NewGuid().ToString("N"), ProjectSlug = slug, TicketId = null,
        AgentName = agent, SkillFile = "executePowerShell", ConcurrencyGroup = agent,
        StartedAt = DateTime.UtcNow,
    };

    private static RunningProcess StartPowerShellRun(AgentRunRegistry runs, string slug, string lateWritePath)
    {
        var run = runs.Register(NewRun(slug, "automation:integration-process"));
        var escapedPath = lateWritePath.Replace("'", "''", StringComparison.Ordinal);
        var command = $"Start-Process powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 3; Set-Content ''{escapedPath}'' leaked'; Start-Sleep -Seconds 30";
        var task = Task.Run(async () =>
        {
            try
            {
                await ProcessRunner.RunAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"",
                    timeout: TimeSpan.FromSeconds(60), ct: run.Cancellation.Token);
                runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            }
            catch (OperationCanceledException)
            {
                runs.Complete(run.RunId, AgentRunStatus.Stopped, null);
            }
        });
        return new RunningProcess(run, task);
    }

    private static async Task<AgentRun> WaitForRunAsync(AgentRunRegistry runs)
    {
        for (var i = 0; i < 200; i++)
        {
            var run = runs.AllActive().FirstOrDefault(r => r.AgentName.StartsWith("automation:", StringComparison.Ordinal));
            if (run?.WorkingDirectory is not null
                && File.Exists(Path.Combine(run.WorkingDirectory, ".agents", "automation-proof", "preserved.txt")))
                return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("The isolated PowerShell automation did not start.");
    }

    private static async Task<AgentRun> WaitForFinishedRunAsync(AgentRunRegistry runs)
    {
        for (var i = 0; i < 300; i++)
        {
            var run = runs.AllForProject("powershell-lifecycle")
                .FirstOrDefault(r => r.AgentName.StartsWith("automation:", StringComparison.Ordinal));
            if (run is not null && run.Status != AgentRunStatus.Running)
                return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("The PowerShell automation did not finish.");
    }

    private static async Task<WorktreeMergeRequest> WaitForMergeRequestAsync(
        WorktreeMergeQueueService queue, string slug, WorktreeMergeStatus status)
    {
        for (var i = 0; i < 200; i++)
        {
            var request = (await queue.ListAsync(slug)).SingleOrDefault();
            if (request?.Status == status) return request;
            await Task.Delay(50);
        }
        var current = (await queue.ListAsync(slug)).SingleOrDefault();
        throw new TimeoutException($"Merge request did not reach {status}; current={current?.Status}.");
    }

    private static string Fingerprint(string repository, string sentinel) =>
        $"{Git(repository, "rev-parse", "HEAD").Trim()}\n{Git(repository, "status", "--porcelain=v1")}{File.ReadAllText(sentinel)}";

    private static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrEmpty(error) || process.ExitCode == 0, error);
        return output;
    }

    public void Dispose()
    {
        foreach (var fixture in _fixtures) fixture.Dispose();
    }

    private sealed record Fixture(TempDir Root, string Repository, string Sentinel, string Slug,
        AgentRunRegistry Runs, WorktreeMergeQueueService Queue, ActionExecutor Executor, ProjectRuntime Runtime) : IDisposable
    {
        public void Dispose() => Root.Dispose();
    }

    private sealed record RunningProcess(AgentRun Run, Task Task);

    public sealed class ApiFactory : WebApplicationFactory<CreateProjectRequest>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(),
            "kittyclaw-lifecycle-api-" + Guid.NewGuid().ToString("N"));

        public ApiFactory()
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "settings.json"),
                """{"OnboardingSeen":true,"Language":"en"}""");
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", DataDir);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", null);
            try { Directory.Delete(DataDir, recursive: true); } catch { }
        }
    }
}
