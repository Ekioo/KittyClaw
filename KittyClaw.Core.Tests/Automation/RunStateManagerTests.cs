using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public class RunStateManagerTests
{
    private static ProjectRuntime MakeRuntime(decimal? dailyBudget = null, int? minDescLen = null)
    {
        var rt = new ProjectRuntime("test-proj");
        rt.Workspace = System.IO.Path.GetTempPath();
        rt.Config = new AutomationConfig
        {
            DailyBudgetUsd = dailyBudget,
            MinDescriptionLength = minDescLen,
            Automations = new(),
        };
        return rt;
    }

    private static RunAgentActionSpec MakeSpec(List<string>? mutuallyExclusive = null) =>
        new() { Agent = "programmer", MutuallyExclusiveWith = mutuallyExclusive ?? new() };

    private static TriggerFiring MakeFiring(int? ticketId = null) =>
        new(ticketId, null, null);

    private static AgentRun MakeRun(string slug, string agent, string group, int? ticketId = null) =>
        new()
        {
            RunId = Guid.NewGuid().ToString(),
            ProjectSlug = slug,
            AgentName = agent,
            SkillFile = $"{agent}/SKILL.md",
            ConcurrencyGroup = group,
            StartedAt = DateTime.UtcNow,
            TicketId = ticketId,
        };

    private static RunStateManager MakeSut(AgentRunRegistry? runs = null) =>
        new(runs ?? new AgentRunRegistry(), new CostTracker(), null!, NullLogger.Instance);

    // ── All clear ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_false_when_no_constraints_are_violated()
    {
        var skip = await MakeSut().ShouldSkipAsync(MakeRuntime(), MakeSpec(), MakeFiring(), "programmer", "programmer");
        Assert.False(skip);
    }

    [Fact]
    public async Task Returns_false_when_runtime_has_no_config()
    {
        var rt = new ProjectRuntime("test-proj");
        rt.Workspace = System.IO.Path.GetTempPath();
        // Config intentionally left null

        var skip = await MakeSut().ShouldSkipAsync(rt, MakeSpec(), MakeFiring(), "programmer", "programmer");
        Assert.False(skip);
    }

    // ── Group concurrency ─────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_true_when_group_is_already_active()
    {
        var runs = new AgentRunRegistry();
        runs.Register(MakeRun("test-proj", "programmer", "programmer"));

        var skip = await MakeSut(runs).ShouldSkipAsync(MakeRuntime(), MakeSpec(), MakeFiring(), "programmer", "programmer");
        Assert.True(skip);
    }

    [Fact]
    public async Task Returns_false_when_group_run_is_in_different_project()
    {
        var runs = new AgentRunRegistry();
        runs.Register(MakeRun("other-proj", "programmer", "programmer"));

        var skip = await MakeSut(runs).ShouldSkipAsync(MakeRuntime(), MakeSpec(), MakeFiring(), "programmer", "programmer");
        Assert.False(skip);
    }

    // ── Same agent already running for the ticket ─────────────────────────────

    [Fact]
    public async Task Returns_true_when_same_agent_is_active_for_ticket()
    {
        var runs = new AgentRunRegistry();
        runs.Register(MakeRun("test-proj", "programmer", "programmer-unique", ticketId: 42));

        // Use a distinct group so group-concurrency doesn't also trip
        var skip = await MakeSut(runs).ShouldSkipAsync(MakeRuntime(), MakeSpec(), MakeFiring(ticketId: 42), "programmer", "programmer-unique");
        Assert.True(skip);
    }

    [Fact]
    public async Task Returns_false_when_different_agent_runs_for_same_ticket()
    {
        var runs = new AgentRunRegistry();
        runs.Register(MakeRun("test-proj", "groomer", "groomer", ticketId: 42));

        var skip = await MakeSut(runs).ShouldSkipAsync(MakeRuntime(), MakeSpec(), MakeFiring(ticketId: 42), "programmer", "programmer");
        Assert.False(skip);
    }

    // ── Mutual exclusion ──────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_true_when_mutually_exclusive_group_is_active()
    {
        var runs = new AgentRunRegistry();
        runs.Register(MakeRun("test-proj", "qa-tester", "qa-tester"));

        var spec = MakeSpec(mutuallyExclusive: new() { "qa-tester" });
        var skip = await MakeSut(runs).ShouldSkipAsync(MakeRuntime(), spec, MakeFiring(), "programmer", "programmer");
        Assert.True(skip);
    }

    [Fact]
    public async Task Returns_false_when_mutually_exclusive_group_is_in_different_project()
    {
        var runs = new AgentRunRegistry();
        runs.Register(MakeRun("other-proj", "qa-tester", "qa-tester"));

        var spec = MakeSpec(mutuallyExclusive: new() { "qa-tester" });
        var skip = await MakeSut(runs).ShouldSkipAsync(MakeRuntime(), spec, MakeFiring(), "programmer", "programmer");
        Assert.False(skip);
    }
}
