using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Ticket #152 replaces first-match-wins with durable discovery of every matching automation.
/// </summary>
[Collection("MockClaude")]
public class ColumnPollFirstMatchTests
{
    private sealed record Harness(
        TriggerHandler Handler,
        TicketService Tickets,
        MemberService Members,
        AutomationStore Store,
        AutomationQueueStore Queue,
        AutomationQueueProcessor Processor,
        AgentRunRegistry Runs,
        string Workspace,
        string Slug);

    private static async Task<Harness> BuildAsync(string dataDir, params AutomationRule[] automations)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("first-match-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 4);
        var runner = new AgentRunner(sessions, runs, gate, NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var loc = new LocalizationService(new AppSettingsService(dataDir));
        var tickets = new TicketService(projects, members);
        var runState = new RunStateManager(runs, cost, tickets, NullLogger.Instance);
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState,
            NullLogger.Instance);

        var store = new AutomationStore(projects);
        await store.SaveAsync(project.Slug, new AutomationConfig { Automations = automations.ToList() });
        var manager = new ProjectRuntimeManager(store, new TriggerStateStore(projects), NullLogger.Instance);
        var queue = new AutomationQueueStore(projects);
        var handler = new TriggerHandler(
            projects, manager, executor, tickets, members, sessions, runs, queue, NullLogger.Instance);
        var processor = new AutomationQueueProcessor(
            projects, tickets, queue, manager, executor, NullLogger.Instance);

        return new Harness(handler, tickets, members, store, queue, processor, runs, workspace, project.Slug);
    }

    private static AutomationRule ColumnPollAutomation(string id, string comment, string? assigneeFilter = null,
        ConditionSpec? condition = null) => new()
    {
        Id = id,
        Name = id,
        Trigger = new TicketInColumnTriggerSpec { Columns = { "Todo" }, Seconds = 30, AssigneeSlug = assigneeFilter },
        Conditions = condition is null ? [] : [condition],
        Actions = [new AddCommentActionSpec { Content = comment, Author = "automation" }],
    };

    private static async Task<string> CreateDelayedScenarioAsync(string root, string name, int delayMs)
    {
        var scenarios = Path.Combine(root, "mock-scenarios");
        Directory.CreateDirectory(scenarios);
        await File.WriteAllTextAsync(Path.Combine(scenarios, $"{name}.ndjson"), string.Join('\n', new[]
        {
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            $"{{\"_meta\":{{\"delay_ms\":{delayMs}}}}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":1,\"num_turns\":1}",
        }));
        return scenarios;
    }

    private static async Task<string> CreateFailedScenarioAsync(string root, string name)
    {
        var scenarios = Path.Combine(root, "mock-scenarios");
        Directory.CreateDirectory(scenarios);
        await File.WriteAllTextAsync(Path.Combine(scenarios, $"{name}.ndjson"), string.Join('\n', new[]
        {
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            "{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"duration_ms\":1,\"num_turns\":1}",
            "{\"_meta\":{\"exit\":1}}",
        }));
        return scenarios;
    }

    private static AutomationRule AgentAutomation(
        string id, string agent, string group, string scenario, string scenariosDir) => new()
    {
        Id = id,
        Name = id,
        Trigger = new TicketInColumnTriggerSpec { Columns = { "Todo" }, Seconds = 0 },
        Actions =
        [
            new RunAgentActionSpec
            {
                Agent = agent,
                MaxTurns = 1,
                ConcurrencyGroup = group,
                Env = new Dictionary<string, string>
                {
                    ["KITTYCLAW_MOCK_SCENARIO"] = scenario,
                    ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenariosDir,
                },
            },
            new AddCommentActionSpec { Content = $"{id}-after-agent", Author = "automation" },
        ],
    };

    private static AutomationRule AgentOnlyAutomation(
        string id, string agent, string scenario, string scenariosDir, int cap = 2) => new()
    {
        Id = id,
        Name = id,
        Trigger = new TicketInColumnTriggerSpec
        {
            Columns = { "Todo" }, Seconds = 0, MaxConsecutiveRuns = cap,
            FailureBackoffSeconds = 1, MaxFailureBackoffSeconds = 4,
        },
        Actions =
        [
            new RunAgentActionSpec
            {
                Agent = agent,
                MaxTurns = 1,
                Env = new Dictionary<string, string>
                {
                    ["KITTYCLAW_MOCK_SCENARIO"] = scenario,
                    ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenariosDir,
                },
            },
        ],
    };

    private static async Task<AutomationQueueEntry> ProcessUntilTerminalAsync(
        Harness h, int ticketId, string automationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await h.Processor.ProcessOnceAsync(CancellationToken.None);
            var entry = (await h.Queue.ListForTicketAsync(h.Slug, ticketId))
                .FirstOrDefault(x => x.AutomationId == automationId);
            if (entry is not null && entry.Status is not AutomationQueueStatus.Pending and not AutomationQueueStatus.Running)
                return entry;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Queued automation '{automationId}' did not finish.");
    }

    [Fact]
    public async Task SameTicket_MatchingTwoColumnPollAutomations_QueuesBothInFileOrder()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first"),
            ColumnPollAutomation("second", "from-second"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Racy ticket", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var entries = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        Assert.Equal(["second", "first"], entries.Select(e => e.AutomationId));
        Assert.Equal([2L, 1L], entries.Select(e => e.QueueOrder));
    }

    [Fact]
    public async Task TicketNotMatchedByFirstTriggerFilter_StaysAvailableToSecond()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first", assigneeFilter: "ghost"),
            ColumnPollAutomation("second", "from-second"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Unassigned ticket", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal("second", Assert.Single(await h.Queue.ListForTicketAsync(h.Slug, ticket.Id)).AutomationId);
    }

    [Fact]
    public async Task TicketFailingFirstAutomationConditions_IsNotConsumed()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first",
                condition: new AssignedToConditionSpec { Slugs = { "ghost" } }),
            ColumnPollAutomation("second", "from-second"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Condition-miss ticket", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal("second", Assert.Single(await h.Queue.ListForTicketAsync(h.Slug, ticket.Id)).AutomationId);
    }

    [Fact]
    public async Task DistinctTickets_AreConsumedIndependently()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path,
            ColumnPollAutomation("first", "from-first", assigneeFilter: "worker"),
            ColumnPollAutomation("second", "from-second"));
        await h.Members.CreateMemberAsync(h.Slug, "worker");
        var assigned = await h.Tickets.CreateTicketAsync(h.Slug, "Assigned", status: "Todo", assignedTo: "worker");
        var unassigned = await h.Tickets.CreateTicketAsync(h.Slug, "Unassigned", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(["second", "first"], (await h.Queue.ListForTicketAsync(h.Slug, assigned.Id)).Select(e => e.AutomationId));
        Assert.Equal("second", Assert.Single(await h.Queue.ListForTicketAsync(h.Slug, unassigned.Id)).AutomationId);
    }

    [Fact]
    public async Task LeavingUnwatchedColumnAndReentering_CreatesANewLogicalOccurrence()
    {
        using var tmp = new TempDir();
        var automation = ColumnPollAutomation("only-todo", "ran");
        ((TicketInColumnTriggerSpec)automation.Trigger).Seconds = 0;
        var h = await BuildAsync(tmp.Path, automation);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Reentry", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "Backlog");
        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Tickets.MoveTicketAsync(h.Slug, ticket.Id, "Todo");
        await h.Handler.ProcessTickAsync(CancellationToken.None);

        var entries = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries.Select(x => x.OccurrenceId).Distinct().Count());
    }

    [Fact]
    public async Task Processor_ExecutesCompleteChainWithoutRunAgent()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path, ColumnPollAutomation("comment-only", "queue-ran"));
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Non-agent chain", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        var entry = await ProcessUntilTerminalAsync(h, ticket.Id, "comment-only");

        Assert.Equal(AutomationQueueStatus.Completed, entry.Status);
        Assert.Contains((await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Comments,
            c => c.Content == "queue-ran");
    }

    [Fact]
    public async Task Processor_RunAgentEntryRemainsRunningUntilAgentAndFollowingActionsFinish()
    {
        using var tmp = new TempDir();
        var scenarios = await CreateDelayedScenarioAsync(tmp.Path, "queue-delay", 1_000);
        var automation = AgentAutomation("agent-chain", "queue-agent", "queue-group", "queue-delay", scenarios);
        var h = await BuildAsync(tmp.Path, automation);
        TestSkillBuilder.Create(h.Workspace, "queue-agent", scenario: "queue-delay");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Agent chain", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Processor.ProcessOnceAsync(CancellationToken.None);
        await Task.Delay(200);

        var running = Assert.Single(await h.Queue.ListForTicketAsync(h.Slug, ticket.Id));
        Assert.Equal(AutomationQueueStatus.Running, running.Status);
        Assert.DoesNotContain((await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Comments,
            c => c.Content == "agent-chain-after-agent");

        var completed = await ProcessUntilTerminalAsync(h, ticket.Id, "agent-chain");
        Assert.Equal(AutomationQueueStatus.Completed, completed.Status);
        Assert.Contains((await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Comments,
            c => c.Content == "agent-chain-after-agent");
    }

    [Fact]
    public async Task Processor_DistinctConcurrencyGroupsRunTogether_WhileSameGroupIsExcluded()
    {
        using var tmp = new TempDir();
        var scenarios = await CreateDelayedScenarioAsync(tmp.Path, "parallel-delay", 1_200);
        var h = await BuildAsync(tmp.Path,
            AgentAutomation("first", "agent-one", "shared", "parallel-delay", scenarios),
            AgentAutomation("second", "agent-two", "distinct", "parallel-delay", scenarios),
            AgentAutomation("third", "agent-three", "shared", "parallel-delay", scenarios));
        TestSkillBuilder.Create(h.Workspace, "agent-one", scenario: "parallel-delay");
        TestSkillBuilder.Create(h.Workspace, "agent-two", scenario: "parallel-delay");
        TestSkillBuilder.Create(h.Workspace, "agent-three", scenario: "parallel-delay");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Concurrency", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Processor.ProcessOnceAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && h.Runs.ActiveForProject(h.Slug).Count() < 2)
            await Task.Delay(50);
        var active = h.Runs.ActiveForProject(h.Slug).ToList();
        Assert.Equal(2, active.Count);
        Assert.Equal(1, active.Count(x => x.ConcurrencyGroup == "shared"));
        Assert.Equal(1, active.Count(x => x.ConcurrencyGroup == "distinct"));

        await ProcessUntilTerminalAsync(h, ticket.Id, "first");
        await ProcessUntilTerminalAsync(h, ticket.Id, "second");
        await ProcessUntilTerminalAsync(h, ticket.Id, "third");
        Assert.Contains((await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Comments,
            c => c.Content == "third-after-agent");
    }

    [Fact]
    public async Task ProcessorMove_ReconcilesAndQueuesAutomationForDestinationColumn()
    {
        using var tmp = new TempDir();
        var move = ColumnPollAutomation("move", "moving");
        move.Actions = [new MoveTicketStatusActionSpec { To = "InProgress" }];
        var destination = ColumnPollAutomation("destination", "arrived");
        ((TicketInColumnTriggerSpec)destination.Trigger).Columns = ["InProgress"];
        ((TicketInColumnTriggerSpec)destination.Trigger).Seconds = 0;
        var h = await BuildAsync(tmp.Path, move, destination);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Moving chain", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(AutomationQueueStatus.Completed,
            (await ProcessUntilTerminalAsync(h, ticket.Id, "move")).Status);
        await h.Handler.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(AutomationQueueStatus.Completed,
            (await ProcessUntilTerminalAsync(h, ticket.Id, "destination")).Status);

        Assert.Equal("InProgress", (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Status);
        Assert.Contains((await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Comments,
            c => c.Content == "arrived");
    }

    [Fact]
    public void FailureBackoff_IsExponentialAndCapped()
    {
        var trigger = new TicketInColumnTriggerSpec
        {
            FailureBackoffSeconds = 10,
            MaxFailureBackoffSeconds = 25,
        };

        Assert.Equal(10, AutomationQueueProcessor.ComputeFailureBackoffSeconds(trigger, 1));
        Assert.Equal(20, AutomationQueueProcessor.ComputeFailureBackoffSeconds(trigger, 2));
        Assert.Equal(25, AutomationQueueProcessor.ComputeFailureBackoffSeconds(trigger, 3));
        Assert.Equal(25, AutomationQueueProcessor.ComputeFailureBackoffSeconds(trigger, 20));
    }

    [Fact]
    public async Task AgentOnly_NoTransition_StopsAtCapWithOneBlockedDiagnostic()
    {
        using var tmp = new TempDir();
        var scenarios = await CreateDelayedScenarioAsync(tmp.Path, "cap-success", 0);
        var automation = AgentOnlyAutomation("bounded-resume", "resume-agent", "cap-success", scenarios);
        var h = await BuildAsync(tmp.Path, automation);
        TestSkillBuilder.Create(h.Workspace, "resume-agent", scenario: "cap-success");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Bounded resume", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        AutomationQueueEntry? terminal = null;
        while (DateTime.UtcNow < deadline)
        {
            await h.Processor.ProcessOnceAsync(CancellationToken.None);
            terminal = Assert.Single(await h.Queue.ListForTicketAsync(h.Slug, ticket.Id));
            if (terminal.Status == AutomationQueueStatus.Cancelled) break;
            await Task.Delay(100);
        }

        Assert.NotNull(terminal);
        Assert.Equal(AutomationQueueStatus.Cancelled, terminal.Status);
        Assert.Equal(2, terminal.Attempts);
        var updated = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("Blocked", updated!.Status);
        Assert.Single(updated.Comments, c => c.Content.Contains("[automation-retry-cap]"));
        Assert.Equal(2, h.Runs.AllForTicket(h.Slug, ticket.Id).Count());
    }

    [Fact]
    public async Task AgentOnly_FailedRunsUseDurableBackoffAndStopAtCap()
    {
        using var tmp = new TempDir();
        var scenarios = await CreateFailedScenarioAsync(tmp.Path, "cap-failure");
        var automation = AgentOnlyAutomation("bounded-failure", "failing-agent", "cap-failure", scenarios, cap: 3);
        var trigger = (TicketInColumnTriggerSpec)automation.Trigger;
        trigger.FailureBackoffSeconds = 1;
        trigger.MaxFailureBackoffSeconds = 2;
        var h = await BuildAsync(tmp.Path, automation);
        TestSkillBuilder.Create(h.Workspace, "failing-agent", scenario: "cap-failure");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Bounded failure", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        var observedDelays = new List<int>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            AutomationQueueEntry? pending = null;
            while (DateTime.UtcNow < deadline)
            {
                await h.Processor.ProcessOnceAsync(CancellationToken.None);
                var current = Assert.Single(await h.Queue.ListForTicketAsync(h.Slug, ticket.Id));
                if (current.Status == AutomationQueueStatus.Pending && current.Attempts == attempt)
                {
                    pending = current;
                    break;
                }
                await Task.Delay(25);
            }
            Assert.NotNull(pending);
            observedDelays.Add((int)Math.Round((pending.AvailableAt!.Value - DateTime.UtcNow).TotalSeconds));
            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }

        var terminal = await ProcessUntilTerminalAsync(h, ticket.Id, "bounded-failure");
        Assert.Equal([1, 2], observedDelays);
        Assert.Equal(AutomationQueueStatus.Cancelled, terminal.Status);
        Assert.Equal(3, terminal.Attempts);
        Assert.All(h.Runs.AllForTicket(h.Slug, ticket.Id), run => Assert.Equal(AgentRunStatus.Failed, run.Status));
        var updated = await h.Tickets.GetTicketAsync(h.Slug, ticket.Id);
        Assert.Equal("Blocked", updated!.Status);
        Assert.Single(updated.Comments, c => c.Content.Contains("[automation-retry-cap]"));
    }

    [Fact]
    public async Task AgentOnly_ManualStopSuppressesAutomaticRetry()
    {
        using var tmp = new TempDir();
        var scenarios = await CreateDelayedScenarioAsync(tmp.Path, "manual-stop", 5_000);
        var automation = AgentOnlyAutomation("stopped-resume", "stopped-agent", "manual-stop", scenarios, cap: 3);
        var h = await BuildAsync(tmp.Path, automation);
        TestSkillBuilder.Create(h.Workspace, "stopped-agent", scenario: "manual-stop");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Stopped resume", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Processor.ProcessOnceAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        AgentRun? active = null;
        while (DateTime.UtcNow < deadline && active is null)
        {
            active = h.Runs.ActiveForProject(h.Slug).SingleOrDefault();
            if (active is null) await Task.Delay(25);
        }
        Assert.NotNull(active);
        active.Cancellation.Cancel();

        var terminal = await ProcessUntilTerminalAsync(h, ticket.Id, "stopped-resume");
        Assert.Equal(AutomationQueueStatus.Cancelled, terminal.Status);
        Assert.Equal(1, terminal.Attempts);
        Assert.Contains("stopped manually", terminal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentRunStatus.Stopped, Assert.Single(h.Runs.AllForTicket(h.Slug, ticket.Id)).Status);
        Assert.Equal("Todo", (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Status);
    }

    [Fact]
    public async Task AgentOnly_NewRelevantCommentResetsConsecutiveAttempts()
    {
        using var tmp = new TempDir();
        var scenarios = await CreateDelayedScenarioAsync(tmp.Path, "input-reset", 500);
        var automation = AgentOnlyAutomation("reset-resume", "reset-agent", "input-reset", scenarios, cap: 3);
        var h = await BuildAsync(tmp.Path, automation);
        TestSkillBuilder.Create(h.Workspace, "reset-agent", scenario: "input-reset");
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Reset resume", status: "Todo");

        await h.Handler.ProcessTickAsync(CancellationToken.None);
        await h.Processor.ProcessOnceAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !h.Runs.ActiveForProject(h.Slug).Any())
            await Task.Delay(25);
        Assert.Single(h.Runs.ActiveForProject(h.Slug));
        await h.Tickets.AddCommentAsync(h.Slug, ticket.Id, "New owner input", "owner");

        AutomationQueueEntry? reset = null;
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = Assert.Single(await h.Queue.ListForTicketAsync(h.Slug, ticket.Id));
            if (current.Status == AutomationQueueStatus.Pending && current.Attempts == 0)
            {
                reset = current;
                break;
            }
            await Task.Delay(25);
        }

        Assert.NotNull(reset);
        Assert.Equal("Todo", (await h.Tickets.GetTicketAsync(h.Slug, ticket.Id))!.Status);
        Assert.Single(h.Runs.AllForTicket(h.Slug, ticket.Id));
    }

    [Fact]
    public async Task OpposingMoveAutomations_DoNotAccumulateAnUnboundedLoop()
    {
        using var tmp = new TempDir();
        var toDoing = ColumnPollAutomation("to-doing", "unused");
        toDoing.Actions = [new MoveTicketStatusActionSpec { To = "InProgress" }];
        ((TicketInColumnTriggerSpec)toDoing.Trigger).Seconds = 0;
        var toTodo = ColumnPollAutomation("to-todo", "unused");
        ((TicketInColumnTriggerSpec)toTodo.Trigger).Columns = ["InProgress"];
        ((TicketInColumnTriggerSpec)toTodo.Trigger).Seconds = 0;
        toTodo.Actions = [new MoveTicketStatusActionSpec { To = "Todo" }];
        var h = await BuildAsync(tmp.Path, toDoing, toTodo);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Loop", status: "Todo");

        for (var i = 0; i < 12; i++)
        {
            await h.Handler.ProcessTickAsync(CancellationToken.None);
            await h.Processor.ProcessOnceAsync(CancellationToken.None);
            await Task.Delay(25);
        }

        var entries = await h.Queue.ListForTicketAsync(h.Slug, ticket.Id);
        Assert.True(entries.Count <= 4,
            $"Opposing move automations created {entries.Count} durable entries without being bounded or cancelled.");
        var cancelled = Assert.Single(entries, x => x.Status == AutomationQueueStatus.Cancelled);
        Assert.Equal(AutomationQueueStore.LoopProtectionReason, cancelled.Reason);
        Assert.Equal(3, entries.Count(x => x.Status == AutomationQueueStatus.Completed));
    }
}
