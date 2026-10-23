using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class ColumnProcessingDependencyIntegrationTests
{
    [Fact]
    public async Task Engine_skips_waiting_card_and_dispatches_it_once_after_all_prerequisites_succeed()
    {
        using var temp = new TempDir();
        var previousScenario = Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO");
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN")));
        Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO", "column-success");
        try
        {
            var projects = new ProjectService(temp.Path);
            var project = await projects.CreateProjectAsync("Dependency engine integration");
            Directory.CreateDirectory(projects.ResolveWorkspacePath(project));
            var columns = new ColumnService(projects);
            var tickets = new TicketService(projects, new MemberService(projects));
            var skills = new ProjectSkillService(projects);
            var processors = new ColumnProcessorService(projects, skills);
            var executions = new ColumnExecutionService(projects, tickets);
            var source = await columns.CreateColumnAsync(project.Slug, "Todo");
            var success = await columns.CreateColumnAsync(project.Slug, "Delivered", role: ColumnRole.Success);
            await processors.SaveAsync(project.Slug, source.Id, "Worker", "Complete the ticket.", "claude-sonnet-4-6",
                true, 1, [], [], [], defaultTargetColumnId: success.Id);
            var first = await tickets.CreateTicketAsync(project.Slug, "First prerequisite");
            var second = await tickets.CreateTicketAsync(project.Slug, "Second prerequisite");
            var waiting = await tickets.CreateTicketAsync(project.Slug, "Waiting", description: "Preserve acceptance criteria.",
                status: source.Name, columnId: source.Id);
            await tickets.AddDependencyAsync(project.Slug, waiting.Id, first.Id);
            await tickets.AddDependencyAsync(project.Slug, waiting.Id, second.Id);
            var eligible = await tickets.CreateTicketAsync(project.Slug, "Eligible", status: source.Name, columnId: source.Id);
            var before = (await tickets.GetTicketAsync(project.Slug, waiting.Id))!;
            var runs = new AgentRunRegistry();
            var runner = new AgentRunner(new SessionRegistry(), runs, new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance);
            var dispatcher = new ColumnAgentDispatcher(runner, projects, skills, processors, null!);
            using var engine = new ColumnProcessingEngine(projects, tickets, processors, executions, dispatcher,
                new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance),
                new ColumnMemoryCapitalizationService(projects), NullLogger<ColumnProcessingEngine>.Instance)
            {
                PeriodicSweepInterval = TimeSpan.FromMilliseconds(100),
            };
            await engine.StartAsync(CancellationToken.None);
            try
            {
                await WaitForAsync(async () => (await tickets.GetTicketAsync(project.Slug, eligible.Id))!.ColumnId == success.Id);
                Assert.Single(runs.AllForTicket(project.Slug, eligible.Id));
                await tickets.MoveTicketAsync(project.Slug, first.Id, success.Name, "owner");
                // Exercise several actual watchdog scans while the second prerequisite remains open.
                await Task.Delay(600);
                Assert.Empty(runs.AllForTicket(project.Slug, waiting.Id));
                Assert.Empty(await executions.ListAsync(project.Slug, waiting.Id));
                var unchanged = (await tickets.GetTicketAsync(project.Slug, waiting.Id))!;
                Assert.Equal(source.Id, unchanged.ColumnId);
                Assert.Equal(before.UpdatedAt, unchanged.UpdatedAt);
                Assert.Equal(before.Description, unchanged.Description);
                Assert.Equal(before.Comments.Count, unchanged.Comments.Count);
                Assert.Equal(before.Activities.Count, unchanged.Activities.Count);
                Assert.Equal(2, unchanged.BlockedBy.Count);

                await tickets.MoveTicketAsync(project.Slug, second.Id, success.Name, "owner");
                await WaitForAsync(async () => (await tickets.GetTicketAsync(project.Slug, waiting.Id))!.ColumnId == success.Id);
                await Task.Delay(400);
                var run = Assert.Single(runs.AllForTicket(project.Slug, waiting.Id));
                Assert.Equal(AgentRunStatus.Completed, run.Status);
                var execution = Assert.Single(await executions.ListAsync(project.Slug, waiting.Id));
                Assert.Equal(ColumnExecutionStatus.Completed, execution.Status);
                Assert.Equal(1, execution.Attempt);
                var delivered = (await tickets.GetTicketAsync(project.Slug, waiting.Id))!;
                Assert.Equal(before.Description, delivered.Description);
                Assert.Equal(2, delivered.BlockedBy.Count);
            }
            finally
            {
                await engine.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO", previousScenario);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("The isolated column engine did not complete the expected transition.");
    }
}
