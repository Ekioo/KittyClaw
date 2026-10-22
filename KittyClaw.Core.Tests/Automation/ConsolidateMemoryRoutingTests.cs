using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public class ConsolidateMemoryRoutingTests
{
    [Theory]
    [InlineData("explicit", "member", "project", "local", "explicit")]
    [InlineData(null, "member", "project", "local", "member")]
    [InlineData(null, null, "project", "local", "project")]
    [InlineData(null, null, null, "local", "local")]
    [InlineData(null, null, null, null, null)]
    public void ModelPriority_IsExplicitThenMemberThenProjectThenProviderDefault(
        string? explicitModel, string? member, string? project, string? local, string? expected)
    {
        Assert.Equal(expected, ActionExecutor.FirstConfiguredModel(explicitModel, member, project, local));
    }

    [Fact]
    public void ConsolidationModel_RoundTripsThroughAutomationJson()
    {
        ActionSpec action = new ConsolidateAgentMemoryActionSpec { Agent = "programmer", Model = "codex:gpt-5.6" };
        var json = JsonSerializer.Serialize(action, AutomationStore.JsonOptions);
        var restored = Assert.IsType<ConsolidateAgentMemoryActionSpec>(
            JsonSerializer.Deserialize<ActionSpec>(json, AutomationStore.JsonOptions));
        Assert.Equal("codex:gpt-5.6", restored.Model);
    }

    [Fact]
    public void ClaudeProviderPrefix_RoutesToClaudeAndIsRemovedFromCliModel()
    {
        var routing = ModelRouting.Resolve("claude:claude-sonnet-4-6", localModelBaseUrl: null);
        var target = routing.ToTarget("claude:claude-sonnet-4-6");

        Assert.Equal(CliProvider.Claude, target.Provider);
        Assert.Equal("claude-sonnet-4-6", target.Model);
        Assert.Null(target.ValidationError);
    }

    [Fact]
    public async Task Consolidation_UsesMemberDefault()
    {
        using var tmp = new TempDir();
        var harness = await BuildAsync(tmp.Path, "consolidate-member");
        var member = await harness.Members.CreateMemberAsync(harness.Project.Slug, "programmer");
        await harness.Members.UpdateMemberAsync(harness.Project.Slug, member.Id, defaultModel: "claude-sonnet-4-6");
        await RunConsolidationAsync(harness, model: null);

        var run = await WaitForConsolidationAsync(harness);
        Assert.Equal("claude-sonnet-4-6", run.Model);
    }

    [Fact]
    public async Task Consolidation_UsesProjectFallbackWhenMemberHasNoModel()
    {
        using var tmp = new TempDir();
        var harness = await BuildAsync(tmp.Path, "consolidate-project");
        await harness.Projects.UpdateProjectAsync(
            harness.Project.Slug, null, fallbackModel: "claude-sonnet-4-6", updateFallback: true);

        await RunConsolidationAsync(harness, model: null);

        var run = await WaitForConsolidationAsync(harness);
        Assert.Equal("claude-sonnet-4-6", run.Model);
    }

    [Fact]
    public async Task UnavailableConsolidationModel_CreatesVisibleActionableFailedRun()
    {
        using var tmp = new TempDir();
        var harness = await BuildAsync(tmp.Path, "consolidate-invalid");

        await RunConsolidationAsync(harness, model: "gemma4");

        var run = await WaitForConsolidationAsync(harness);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal("gemma4", run.Model);
        Assert.Contains(run.SnapshotBuffer(), e =>
            e.Kind == "error" && e.Text.Contains("LocalModelBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailablePrimaryModel_RetriesProjectFallbackWithVisibleDiagnostic()
    {
        using var tmp = new TempDir();
        var harness = await BuildAsync(tmp.Path, "consolidate-fallback");
        await harness.Projects.UpdateProjectAsync(
            harness.Project.Slug, null, fallbackModel: "claude:claude-sonnet-4-6", updateFallback: true);

        Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_UNAVAILABLE_MODEL", "retired-model");
        try
        {
            await RunConsolidationAsync(harness, model: "claude:retired-model");
            var run = await WaitForConsolidationAsync(harness);

            Assert.True(run.Status == AgentRunStatus.Completed, Describe(run));
            Assert.Equal("claude-sonnet-4-6", run.Model);
            Assert.Contains(run.SnapshotBuffer(), e =>
                e.Kind == "fallback" &&
                e.Text.Contains("retired-model", StringComparison.Ordinal) &&
                e.Text.Contains("claude-sonnet-4-6", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_UNAVAILABLE_MODEL", null);
        }
    }

    [Fact]
    public async Task ConsolidationWithNoDurableLesson_LeavesMemoryByteForByteUnchanged()
    {
        using var tmp = new TempDir();
        var harness = await BuildAsync(tmp.Path, "consolidate-no-change");
        var memoryDir = Path.Combine(harness.Runtime.Workspace!, ".agents", "programmer", "memory");
        Directory.CreateDirectory(memoryDir);
        var indexPath = Path.Combine(memoryDir, "MEMORY.md");
        var topicPath = Path.Combine(memoryDir, "existing.md");
        await File.WriteAllBytesAsync(indexPath, "# Existing index\r\n"u8.ToArray());
        await File.WriteAllBytesAsync(topicPath, "Existing lesson\n"u8.ToArray());
        var before = Directory.EnumerateFiles(memoryDir)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);

        await RunConsolidationAsync(harness, model: "claude:claude-sonnet-4-6");
        var run = await WaitForConsolidationAsync(harness);

        Assert.True(run.Status == AgentRunStatus.Completed, Describe(run));
        var after = Directory.EnumerateFiles(memoryDir)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, bytes) in before)
            Assert.Equal(bytes, after[name]);
    }

    [Fact]
    public async Task CanonicalChain_UpdatesAndCommitsMemoryWithoutChangingSuccessfulMainRun()
    {
        using var tmp = new TempDir();
        var harness = await BuildAsync(tmp.Path, "consolidate-chain");
        var workspace = harness.Runtime.Workspace!;
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".agents", "memory-consolidation.md"),
            "# Consolidate {agentSlug}\n\n<!--scenario:memory-update-->");
        RunGit(workspace, "init");
        RunGit(workspace, "config user.email test@example.invalid");
        RunGit(workspace, "config user.name KittyClaw Test");
        RunGit(workspace, "add .agents");
        RunGit(workspace, "commit -m baseline");

        var automation = new AutomationRule
        {
            Id = "canonical-memory-chain",
            Enabled = true,
            Trigger = new StatusChangeTriggerSpec { From = "Todo", To = "Done", PollSeconds = 30 },
            Conditions = [],
            Actions =
            [
                new RunAgentActionSpec { Agent = "programmer", MaxTurns = 1 },
                new ConsolidateAgentMemoryActionSpec { Agent = "programmer", MaxTurns = 1 },
                new CommitAgentMemoryActionSpec { Agent = "programmer" },
            ],
        };

        await harness.Executor.ExecuteAutomationAsync(
            harness.Runtime, automation, new TriggerFiring(null, null, "Done"), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline &&
               !RunGit(workspace, "log -1 --pretty=%s").Contains("chore(memory): programmer", StringComparison.Ordinal))
            await Task.Delay(50);

        var runDiagnostics = string.Join("\n---\n", harness.Runs.AllForProject(harness.Project.Slug).Select(Describe));
        Assert.True(
            RunGit(workspace, "log -1 --pretty=%s").Contains("chore(memory): programmer", StringComparison.Ordinal),
            runDiagnostics + "\nGit status:\n" + RunGit(workspace, "status --porcelain"));

        var mainRun = harness.Runs.AllForProject(harness.Project.Slug)
            .Single(r => r.ConcurrencyGroup == "programmer");
        var consolidation = await WaitForConsolidationAsync(harness);
        Assert.Equal(AgentRunStatus.Completed, mainRun.Status);
        Assert.Equal(AgentRunStatus.Completed, consolidation.Status);
        Assert.True(File.Exists(Path.Combine(workspace, ".agents", "programmer", "memory", "routing.md")));
        Assert.Contains("Routing", await File.ReadAllTextAsync(
            Path.Combine(workspace, ".agents", "programmer", "memory", "MEMORY.md")));
        Assert.Equal(string.Empty, RunGit(workspace, "status --porcelain -- .agents/programmer/memory"));
        Assert.Contains("chore(memory): programmer", RunGit(workspace, "log -1 --pretty=%s"));
        Assert.Equal("programmer@kittyclaw.local", RunGit(workspace, "log -1 --pretty=%ae"));
    }

    [Fact]
    public async Task PipelineConsolidation_WithWorktrees_QueuesMemoryWithoutDirtyingPrimaryCheckout()
    {
        using var tmp = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(tmp.Path, "integration");
        TestSkillBuilder.Create(repository, "programmer", "default");
        var agents = Path.Combine(repository, ".agents");
        var memory = Path.Combine(agents, "programmer", "memory");
        Directory.CreateDirectory(memory);
        await File.WriteAllTextAsync(
            Path.Combine(agents, "memory-consolidation.md"),
            "# Consolidate {agentSlug}\n\n<!--scenario:memory-update-->");
        await File.WriteAllTextAsync(Path.Combine(memory, "MEMORY.md"), "# Memory\n");
        RunGit(repository, "add .agents");
        RunGit(repository, "commit -m baseline-memory");

        var projects = new ProjectService(Path.Combine(tmp.Path, "data"));
        var project = (await projects.CreateProjectAsync("pipeline memory routing"))!;
        await projects.UpdateProjectAsync(project.Slug, repository);
        await projects.UpdateProjectAsync(project.Slug, null, worktreesEnabled: true,
            integrationBranch: "integration");
        var members = new MemberService(projects);
        await members.CreateMemberAsync(project.Slug, "programmer");
        var tickets = new TicketService(projects, members);
        var sessions = new SessionRegistry(tmp.Path);
        var runs = new AgentRunRegistry();
        var cost = new CostTracker();
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4),
            NullLogger<AgentRunner>.Instance);
        var worktrees = new TicketWorktreeService(projects, tickets);
        var queue = new WorktreeMergeQueueService(projects, worktrees);
        var router = new DurableWriteRouter(projects, worktrees, queue);
        var executor = new ActionExecutor(
            tickets, members, new LabelService(projects), sessions, runs, runner, cost,
            new LocalizationService(new AppSettingsService(tmp.Path)), projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance),
            NullLogger.Instance, durableWrites: router);
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = repository,
            Config = new AutomationConfig { Automations = [] },
        };
        var primaryBefore = RunGit(repository, "rev-parse HEAD") + "\n" +
            RunGit(repository, "status --porcelain --untracked-files=all");

        await executor.ExecuteAutomationAsync(runtime, new AutomationRule
        {
            Id = "routed-memory",
            Enabled = true,
            Trigger = new StatusChangeTriggerSpec { From = "Todo", To = "Done" },
            Actions = [new ConsolidateAgentMemoryActionSpec { Agent = "programmer", MaxTurns = 1 }],
        }, new TriggerFiring(null, null, "Done"), CancellationToken.None);

        var consolidation = await WaitForFinishedConsolidationAsync(runs, project.Slug);
        var request = await WaitForPendingMergeAsync(queue, project.Slug);
        var primaryAfter = RunGit(repository, "rev-parse HEAD") + "\n" +
            RunGit(repository, "status --porcelain --untracked-files=all");

        Assert.Equal(AgentRunStatus.Completed, consolidation.Status);
        Assert.Equal(primaryBefore, primaryAfter);
        Assert.True(File.Exists(Path.Combine(
            request.WorktreePath, ".agents", "programmer", "memory", "routing.md")));
        Assert.Contains("chore(memory): programmer", RunGit(request.WorktreePath, "log -1 --pretty=%s"));
    }

    private static async Task RunConsolidationAsync(Harness harness, string? model)
    {
        var automation = new AutomationRule
        {
            Id = "memory-test",
            Enabled = true,
            Trigger = new StatusChangeTriggerSpec { From = "Todo", To = "Done", PollSeconds = 30 },
            Conditions = [],
            Actions = [new ConsolidateAgentMemoryActionSpec { Agent = "programmer", Model = model, MaxTurns = 1 }],
        };
        await harness.Executor.ExecuteAutomationAsync(
            harness.Runtime, automation, new TriggerFiring(null, null, "Done"), CancellationToken.None);
    }

    private static async Task<AgentRun> WaitForConsolidationAsync(Harness harness)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cts.IsCancellationRequested)
        {
            var run = harness.Runs.AllForProject(harness.Project.Slug)
                .FirstOrDefault(r => r.ConcurrencyGroup == "consolidate-programmer" && r.Status != AgentRunStatus.Running);
            if (run is not null) return run;
            await Task.Delay(50, cts.Token);
        }
        throw new TimeoutException("Consolidation run did not finish.");
    }

    private static async Task<AgentRun> WaitForFinishedConsolidationAsync(
        AgentRunRegistry runs, string projectSlug)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!cts.IsCancellationRequested)
        {
            var run = runs.AllForProject(projectSlug)
                .FirstOrDefault(candidate => candidate.ConcurrencyGroup == "consolidate-programmer" &&
                    candidate.Status != AgentRunStatus.Running);
            if (run is not null) return run;
            await Task.Delay(50, cts.Token);
        }
        throw new TimeoutException("Routed consolidation run did not finish.");
    }

    private static async Task<WorktreeMergeRequest> WaitForPendingMergeAsync(
        WorktreeMergeQueueService queue, string projectSlug)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var request = (await queue.ListAsync(projectSlug)).SingleOrDefault();
                if (request?.Status == WorktreeMergeStatus.Pending) return request;
                await Task.Delay(50, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        var current = (await queue.ListAsync(projectSlug)).SingleOrDefault();
        throw new TimeoutException(
            $"Memory merge request did not become pending; current={current?.Status}, error={current?.Error}.");
    }

    private static async Task<Harness> BuildAsync(string dataDir, string slug)
    {
        var projects = new ProjectService(dataDir);
        var project = (await projects.CreateProjectAsync(slug))!;
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "programmer", "default");
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".agents", "memory-consolidation.md"),
            "# Consolidate {agentSlug}\n\n<!--scenario:default-->");

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4), NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var settings = new AppSettingsService(dataDir);
        var localization = new LocalizationService(settings);
        var tickets = new TicketService(projects, members);
        var runState = new RunStateManager(runs, cost, tickets, NullLogger.Instance);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, localization, projects, runState,
            NullLogger.Instance);
        var runtime = new ProjectRuntime(project.Slug)
        {
            Workspace = workspace,
            Config = new AutomationConfig { Automations = [] },
        };
        return new Harness(executor, runtime, runs, projects, members, project);
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed: {stderr}");
        return stdout.Trim();
    }

    private static string Describe(AgentRun run) => string.Join(
        Environment.NewLine,
        run.SnapshotBuffer().Select(e => $"{e.Kind}: {e.Text} {e.Detail}"));

    private sealed record Harness(
        ActionExecutor Executor,
        ProjectRuntime Runtime,
        AgentRunRegistry Runs,
        ProjectService Projects,
        MemberService Members,
        KittyClaw.Core.Models.Project Project);
}
