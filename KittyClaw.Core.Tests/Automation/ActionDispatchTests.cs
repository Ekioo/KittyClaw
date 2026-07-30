using System.Text.RegularExpressions;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// §2.1 (backport analysis): a createTicket (or moveTicketStatus) placed AFTER a runAgent
/// used to fall through a second, incomplete switch with no default — a documented feature
/// that silently did nothing. The fix routes every non-runAgent action through ONE shared
/// dispatch (ExecuteChainActionAsync) whose default throws.
///
/// The reflection tests below are the durability guarantee the fix alone can't give:
/// whenever a new ActionSpec subclass is added, they fail until it gets its case in the
/// single dispatch — forgetting is no longer possible.
/// </summary>
[Collection("MockClaude")]
public class ActionDispatchTests
{
    // ── Structural guards (reflection over ActionSpec subclasses) ────────────

    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string ExecutorSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Core", "Automation", "ActionExecutor.cs"));

    private static IReadOnlyList<Type> ConcreteActionTypes() =>
        typeof(ActionSpec).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ActionSpec).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();

    /// <summary>The body of ExecuteChainActionAsync (up to the next private member).</summary>
    private static string DispatchRegion()
    {
        var src = ExecutorSource();
        var start = src.IndexOf("private async Task<bool> ExecuteChainActionAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "ActionExecutor must define the single shared dispatch ExecuteChainActionAsync.");
        var end = src.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        return end > start ? src[start..end] : src[start..];
    }

    [Fact]
    public void EveryActionType_HasACaseInTheSharedDispatch()
    {
        var region = DispatchRegion();
        foreach (var type in ConcreteActionTypes())
            Assert.True(Regex.IsMatch(region, $@"case\s+{Regex.Escape(type.Name)}\b"),
                $"{type.Name} has no case in ExecuteChainActionAsync — a chain containing it " +
                "would throw (or worse, silently do nothing if the dispatch loses its default). " +
                "Add its case to the single shared dispatch.");
    }

    [Fact]
    public void ActionCases_LiveOnlyInTheSharedDispatch()
    {
        // The whole point of the single dispatch is that there is exactly ONE place to
        // register an action. If a per-path switch grows its own action cases again, the
        // two paths can drift apart — the §2.1 bug shape.
        var src = ExecutorSource();
        var region = DispatchRegion();
        foreach (var type in ConcreteActionTypes().Where(t => t != typeof(RunAgentActionSpec)))
        {
            var total = Regex.Matches(src, $@"case\s+{Regex.Escape(type.Name)}\b").Count;
            var inDispatch = Regex.Matches(region, $@"case\s+{Regex.Escape(type.Name)}\b").Count;
            Assert.True(total == inDispatch,
                $"{type.Name} is matched by a switch outside ExecuteChainActionAsync — " +
                "register actions only in the shared dispatch.");
        }
    }

    [Fact]
    public void SharedDispatch_ThrowsOnUnregisteredTypes()
    {
        // No silent fall-through, ever: pre-run lets the throw propagate, post-run turns
        // it into a logged warning through its per-action catch.
        var region = DispatchRegion();
        Assert.Contains("default:", region);
        Assert.Contains("NotSupportedException", region);
    }

    // ── Behavioral: post-run actions actually execute (MockClaude) ───────────

    private sealed record Harness(
        ActionExecutor Executor, ProjectRuntime Runtime, TicketService Tickets,
        SessionRegistry Sessions, AgentRunRegistry Runs, MemberService Members, string Slug, int TicketId);

    private static async Task<Harness> BuildAsync(string dataDir)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("post-run-dispatch-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "worker", scenario: "default");

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4), NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var loc = new LocalizationService(new AppSettingsService(dataDir));
        var tickets = new TicketService(projects, members);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance), NullLogger.Instance);

        var ticket = await tickets.CreateTicketAsync(project.Slug, "Chain ticket", "", "owner");
        await tickets.MoveTicketAsync(project.Slug, ticket.Id, "Done", "automation");

        var rt = new ProjectRuntime(project.Slug) { Workspace = workspace, Config = new AutomationConfig() };
        return new Harness(executor, rt, tickets, sessions, runs, members, project.Slug, ticket.Id);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static Task RunChainAsync(Harness h, params ActionSpec[] actions)
    {
        var automation = new AutomationRule
        {
            Id = "post-run-chain",
            Enabled = true,
            Trigger = new StatusChangeTriggerSpec { To = "Done", PollSeconds = 30 },
            Actions = actions.ToList(),
        };
        var firing = new TriggerFiring(h.TicketId, "Chain ticket", "Done");
        return h.Executor.ExecuteAutomationAsync(h.Runtime, automation, firing, CancellationToken.None);
    }

    [Fact]
    public async Task CreateTicket_AfterRunAgent_ActuallyCreatesTheTicket()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        await RunChainAsync(h,
            new RunAgentActionSpec { Agent = "worker", MaxTurns = 1 },
            new CreateTicketActionSpec { Title = "Follow-up from chain", Status = "Todo" });

        var created = await WaitForAsync(async () =>
            (await h.Tickets.ListTicketsAsync(h.Slug)).Any(t => t.Title == "Follow-up from chain"));
        Assert.True(created, "createTicket placed after runAgent must create the ticket (was a silent no-op).");
    }

    [Fact]
    public async Task MoveTicketStatus_AfterRunAgent_ActuallyMovesTheTicket()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);

        await RunChainAsync(h,
            new RunAgentActionSpec { Agent = "worker", MaxTurns = 1 },
            new MoveTicketStatusActionSpec { To = "InProgress" });

        var moved = await WaitForAsync(async () =>
            (await h.Tickets.GetTicketAsync(h.Slug, h.TicketId))?.Status == "InProgress");
        Assert.True(moved, "moveTicketStatus placed after runAgent must move the ticket (was a silent no-op).");
    }
}
