using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class AgentBudgetTests
{
    private sealed class Harness : IDisposable
    {
        private readonly TempDir _temp = new();
        public ProjectService Projects { get; }
        public AgentRunRegistry Runs { get; } = new();
        public SessionRegistry Sessions { get; }
        public CostTracker Costs { get; }
        public AgentBudgetService Budgets { get; }
        public string Slug { get; private set; } = "";
        public string Workspace { get; private set; } = "";

        public Harness()
        {
            Projects = new(_temp.Path);
            Sessions = new(_temp.Path);
            Costs = new(Sessions);
            Budgets = new(Projects, Costs, Runs);
        }

        public async Task InitializeAsync(decimal? limit = 1m)
        {
            var project = await Projects.CreateProjectAsync("Budget fixture");
            Slug = project.Slug;
            Workspace = Projects.ResolveWorkspacePath(project);
            await SetLimitAsync(limit);
            TestSkillBuilder.Create(Workspace, "budget-agent", "default");
        }

        public Task SetLimitAsync(decimal? limit) => SetConfigAsync(JsonSerializer.Serialize(new { dailyBudgetUsd = limit, automations = Array.Empty<object>() }));
        public Task SetConfigAsync(string config)
        {
            var directory = Path.Combine(Workspace, ".agents");
            Directory.CreateDirectory(directory);
            return File.WriteAllTextAsync(Path.Combine(directory, "automations.json"), config);
        }

        public CostLogEntry Charge(decimal amount, string? id = null, DateTime? at = null) => new(
            at ?? DateTime.UtcNow, "fixture", null, "mock", 0, 0, 0, 0, amount, 0, 0,
            ProjectSlug: Slug, RunId: id);

        public AgentRunner Runner(RunConcurrencyGate? concurrency = null) => new(
            Sessions, Runs, concurrency ?? new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance,
            projects: Projects, budgets: Budgets);

        public AgentRunContext Context(string? scope = null, CliProvider provider = CliProvider.Claude,
            Action<StreamEvent>? onEvent = null, AgentDispatchTarget? fallback = null) => new()
        {
            ProjectSlug = Slug, WorkspacePath = Workspace, AgentName = "budget-agent",
            SkillFile = "budget-agent/SKILL.md", ConcurrencyGroup = "ceo", SessionScope = scope,
            Target = new("claude-primary", provider, new Dictionary<string, string>()),
            FallbackTarget = fallback, MaxTurns = 1, MaxRunDuration = TimeSpan.FromSeconds(15),
            OnEventHook = onEvent, PersistSession = false, PresetRunId = Guid.NewGuid().ToString("N")
        };

        public void Dispose() => _temp.Dispose();
    }

    [Theory]
    [InlineData(CliProvider.Claude, null)]
    [InlineData(CliProvider.Claude, "column-processor")]
    [InlineData(CliProvider.Claude, "chat")]
    [InlineData(CliProvider.Claude, "dashboard")]
    [InlineData(CliProvider.Claude, "memory-consolidation")]
    [InlineData(CliProvider.Codex, "chat")]
    [InlineData(CliProvider.Grok, "column-processor")]
    [InlineData(CliProvider.Mistral, "dashboard")]
    [InlineData(CliProvider.DeepSeek, null)]
    public async Task ExhaustedBudget_BlocksEveryRunScopeAndProviderBeforeLaunch(CliProvider provider, string? scope)
    {
        using var h = new Harness();
        await h.InitializeAsync();
        h.Costs.LogRun(h.Workspace, h.Charge(1m));

        var run = await h.Runner().RunAsync(h.Context(scope, provider), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.NotNull(run.EndedAt);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("daily budget reached"));
        Assert.DoesNotContain(run.SnapshotBuffer(), e => e.Kind == "launch");
        Assert.Null(run.CliVersion);
        Assert.True(h.Runs.TryBeginUpdate());
        h.Runs.CancelUpdate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MissingOrNonPositiveLimit_RemainsUnlimited(int? limit)
    {
        using var h = new Harness();
        await h.InitializeAsync(limit);
        h.Costs.LogRun(h.Workspace, h.Charge(100m));
        Assert.True((await h.Budgets.CheckAsync(h.Slug)).Allowed);
        File.Delete(Path.Combine(h.Workspace, ".agents", "automations.json"));
        Assert.True((await h.Budgets.CheckAsync(h.Slug)).Allowed);
    }

    [Fact]
    public async Task BudgetUsesUtcDayAndRotatedJournalWithoutDoubleCountingUpgradedEntries()
    {
        using var h = new Harness();
        await h.InitializeAsync(3m);
        var old = h.Charge(2m, "upgraded");
        h.Costs.LogRun(h.Workspace, old);
        var path = h.Costs.CurrentLogPath(h.Workspace);
        File.Move(path, path.Replace("cost-log.jsonl", "cost-log-archive.jsonl"));
        h.Costs.LogRun(h.Workspace, old with { UsageVersion = 2 });
        h.Costs.LogRun(h.Workspace, h.Charge(100m, "yesterday", DateTime.UtcNow.Date.AddTicks(-1)));

        var decision = await h.Budgets.CheckAsync(h.Slug);
        Assert.True(decision.Allowed);
        Assert.Equal(2m, decision.SpentUsd);
        h.Costs.LogRun(h.Workspace, h.Charge(1m, "today"));
        Assert.False((await h.Budgets.CheckAsync(h.Slug)).Allowed);
    }

    [Fact]
    public async Task KnownRunUsage_BlocksBeforeAsyncRecording_AndIsNotCountedTwiceAfterward()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        var context = h.Context();
        var run = new AgentRun
        {
            RunId = context.PresetRunId!, ProjectSlug = h.Slug, AgentName = "fixture", SkillFile = "fixture",
            ConcurrencyGroup = "fixture", TicketId = null, StartedAt = DateTime.UtcNow
        };
        h.Runs.Register(run);
        run.AddUsage(1, 1, 0, 0, 1m);
        Assert.False((await h.Budgets.CheckAsync(h.Slug)).Allowed);
        h.Runs.Complete(run.RunId, AgentRunStatus.Completed, 0);
        Assert.Equal(1m, (await h.Budgets.CheckAsync(h.Slug)).SpentUsd);
        h.Costs.LogRun(h.Workspace, h.Charge(1m, run.RunId, run.EndedAt) with { StartedAt = run.StartedAt });
        Assert.Equal(1m, (await h.Budgets.CheckAsync(h.Slug)).SpentUsd);
    }

    [Fact]
    public async Task ConfigurationEditsAreObservedAndInvalidConfigurationFailsClosed()
    {
        using var h = new Harness();
        await h.InitializeAsync(2m);
        h.Costs.LogRun(h.Workspace, h.Charge(1m));
        Assert.True((await h.Budgets.CheckAsync(h.Slug)).Allowed);
        await h.SetLimitAsync(1m);
        Assert.False((await h.Budgets.CheckAsync(h.Slug)).Allowed);
        await h.SetConfigAsync("{ invalid json");
        var failed = await h.Budgets.CheckAsync(h.Slug);
        Assert.False(failed.Allowed);
        Assert.Contains("could not be checked", failed.Reason);
        Assert.False((await h.Budgets.CheckAsync("missing-project")).Allowed);
    }

    [Fact]
    public async Task UnreadableConfiguration_DoesNotDisableTheBudget()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        var config = Path.Combine(h.Workspace, ".agents", "automations.json");
        File.Delete(config);
        Directory.CreateDirectory(config);

        var decision = await h.Budgets.CheckAsync(h.Slug);

        Assert.False(decision.Allowed);
        Assert.Contains("could not be checked", decision.Reason);
    }

    [Fact]
    public async Task ApplicationServices_EnforceBudgetOnTheRegisteredRunner()
    {
        using var factory = new KittyClaw.Core.Tests.Api.TicketLabelsPatchTests.ApiFactory();
        var services = factory.Services;
        var projects = services.GetRequiredService<ProjectService>();
        var project = await projects.CreateProjectAsync("Application budget fixture");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".agents", "automations.json"),
            """{"dailyBudgetUsd":1,"automations":[]}""");
        services.GetRequiredService<CostTracker>().LogRun(workspace,
            new CostLogEntry(DateTime.UtcNow, "fixture", null, "mock", 0, 0, 0, 0, 1m, 0, 0));
        Assert.False((await services.GetRequiredService<AgentBudgetService>().CheckAsync(project.Slug)).Allowed);

        var run = await services.GetRequiredService<AgentRunner>().RunAsync(new AgentRunContext
        {
            ProjectSlug = project.Slug, WorkspacePath = workspace, AgentName = "fixture",
            SkillFile = "(fixture)", InlineSkillContent = "Do not execute.", SessionScope = "chat",
            Target = AgentDispatchTarget.ClaudeDefault
        }, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("daily budget reached"));
        Assert.DoesNotContain(run.SnapshotBuffer(), e => e.Kind == "launch");
    }

    [Fact]
    public async Task ProjectsHaveIndependentBudgets()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        h.Costs.LogRun(h.Workspace, h.Charge(10m));
        var other = await h.Projects.CreateProjectAsync("Other project");
        Assert.True((await h.Budgets.CheckAsync(other.Slug)).Allowed);
    }

    [Fact]
    public async Task ContextCannotBypassTheBudgetByPointingAtAnotherWorkspace()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        h.Costs.LogRun(h.Workspace, h.Charge(1m));
        using var other = new TempDir();
        var run = await h.Runner().RunAsync(new AgentRunContext
        {
            ProjectSlug = h.Slug, WorkspacePath = other.Path, ExecutionWorkspacePath = other.Path,
            AgentName = "fixture", SkillFile = "missing", SessionScope = "chat",
            Target = AgentDispatchTarget.ClaudeDefault
        }, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("daily budget reached"));
        Assert.Null(run.WorkingDirectory);
    }

    [Fact]
    public async Task AvailableBudget_AllowsMockAgentExecution()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        var run = await h.Runner().RunAsync(h.Context("chat"), CancellationToken.None);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Single(run.SnapshotBuffer(), e => e.Kind == "launch");
    }

    [Fact]
    public async Task QueuedRun_RechecksBudgetAfterReceivingItsSlot()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        var concurrency = new RunConcurrencyGate(1);
        using var held = await concurrency.AcquireAsync(false, "held", CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = h.Runner(concurrency).RunAsync(h.Context(), timeout.Token);
        while (concurrency.Snapshot().Queued == 0 && !task.IsCompleted)
            await Task.Delay(20, timeout.Token);
        Assert.False(task.IsCompleted);
        h.Costs.LogRun(h.Workspace, h.Charge(1m));
        held.Dispose();

        var run = await task;
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.DoesNotContain(run.SnapshotBuffer(), e => e.Kind == "launch");
        Assert.Equal(0, concurrency.Snapshot().Active);
    }

    [Fact]
    public async Task Fallback_RechecksBudgetBeforeLaunchingAnotherProviderAttempt()
    {
        using var h = new Harness();
        await h.InitializeAsync();
        TestSkillBuilder.Create(h.Workspace, "budget-agent", "quota");
        var context = h.Context(onEvent: e =>
        {
            if (e.Kind == "result") h.Costs.LogRun(h.Workspace, h.Charge(1m, "concurrent-charge"));
        }, fallback: new("claude-fallback", CliProvider.Claude, new Dictionary<string, string>()));

        var run = await h.Runner().RunAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Single(run.SnapshotBuffer(), e => e.Kind == "launch");
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "fallback");
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("daily budget reached"));
    }

    [Fact]
    public async Task SteeringReplay_AccountsForCurrentRunUsageBeforeAnotherLaunch()
    {
        using var h = new Harness();
        await h.InitializeAsync(0.02m);
        string? runId = null;
        var context = h.Context("chat", onEvent: e =>
        {
            if (e.Kind == "assistant") h.Runs.Get(runId!)!.AddPendingSteerMessage("Additional instruction");
        });
        runId = context.PresetRunId;

        var run = await h.Runner().RunAsync(context, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Single(run.SnapshotBuffer(), e => e.Kind == "launch");
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "steer_replay");
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "error" && e.Text.Contains("daily budget reached"));
    }
}
