using System.Net;
using System.Text.Json.Nodes;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class AutomaticJevColumnRoutingTests
{
    [Theory]
    [InlineData("route", true)]
    [InlineData("assessments", true)]
    [InlineData("uncertain", false)]
    [InlineData("none", false)]
    [InlineData("api-error", false)]
    [InlineData("missing-key", false)]
    [InlineData("stale", false)]
    [InlineData("audit-failure", false)]
    [InlineData("success-without-evidence", false)]
    public async Task Agent_finishes_before_jev_routing_and_uncertain_or_unavailable_jev_uses_classic_routing(string scenario, bool moves)
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Automatic JEV route");
        var columns = new ColumnService(projects);
        var board = await columns.ListColumnsAsync(project.Slug);
        var source = board[0];
        var original = board[1];
        var chosen = await columns.CreateColumnAsync(project.Slug, "JEV destination", pipelineId: source.PipelineId,
            role: scenario == "success-without-evidence" ? ColumnRole.Success : ColumnRole.Normal);
        var tickets = new TicketService(projects, new MemberService(projects));
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(project.Slug, source.Id, "Worker", "Process and route the ticket", null,
            true, 20, [], [], [], defaultTargetColumnId: original.Id, routes: [new("alternative", chosen.Id)],
            afterActions: [new("after", new AddCommentActionSpec { Content = "Action after routing" })],
            evaluateAcceptanceCriteria: scenario == "assessments");
        Assert.False(processor.JevAutomaticRouting);
        Assert.True(processor.UsesAutomaticJevRouting);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Choose the right next step", status: source.Name,
            pipelineId: source.PipelineId, columnId: source.Id);
        if (scenario == "assessments") await tickets.SaveAcceptanceCriterionAsync(project.Slug, ticket.Id, null, new("Checks pass"));
        var executions = new ColumnExecutionService(projects, tickets);
        var execution = (await executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow))!;
        if (scenario == "success-without-evidence")
        {
            await using var db = projects.GetProjectDb(project.Slug);
            (await db.ColumnExecutions.FindAsync(execution.Id))!.TriggerTicketUpdatedAt = null;
            await db.SaveChangesAsync();
        }
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        if (scenario != "missing-key") await vault.SetAsync(project.Slug, JevQualificationService.SecretName, "fixture-key");
        var configuration = new JevConfigurationService(vault, temp.Path, () => null, new TestSecretProtector());
        var dispatcher = new Dispatcher();
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(1, dispatcher.Calls);
            if (scenario == "api-error") return new(HttpStatusCode.ServiceUnavailable);
            Assert.Equal(source.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
            var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("original", input["state"]!["agentClaims"]!["outcome"]!.GetValue<string>());
            if (scenario == "assessments")
            {
                var saved = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
                Assert.Equal(AcceptanceStatus.PASS, Assert.Single(saved.AcceptanceCriteria).Status);
                Assert.Equal("All checks passed", saved.AcceptanceCriteria[0].Evidence?.Content);
                Assert.True(Assert.Single(await executions.ListAsync(project.Slug, ticket.Id)).AgentCompleted);
                Assert.Equal("PASS", input["state"]!["ticket"]!["acceptanceCriteria"]![0]!["status"]!.GetValue<string>());
                Assert.Equal("All checks passed", input["state"]!["ticket"]!["acceptanceCriteria"]![0]!["evidence"]!["content"]!.GetValue<string>());
            }
            var options = input["questions"]!["routing"]!["criteria"]!.AsObject().Select(p => p.Key).ToArray();
            var choice = scenario == "none" ? "none" : $"column-{chosen.Id}";
            var answers = System.Text.Json.JsonSerializer.SerializeToNode(JevResponseFixture.Answers(choice))!;
            answers["routing"]!["confidence"] = scenario == "uncertain" ? .5 : .95;
            answers["routing"]!["probabilities"] = System.Text.Json.JsonSerializer.SerializeToNode(options.ToDictionary(k => k, k => k == choice ? 1.0 : 0.0));
            if (scenario == "stale") await tickets.UpdateTicketAsync(project.Slug, ticket.Id, description: "Owner changed the request");
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["model"] = "jev-fixture", ["answers"] = answers }.ToJsonString()) };
        }));
        var qualification = new JevQualificationService(projects, vault, client, configuration);
        if (scenario == "audit-failure")
        {
            await new JevDecisionStore(projects).ListAsync(project.Slug, ticket.Id);
            await using var db = projects.GetProjectDb(project.Slug);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_jev_audit BEFORE INSERT ON JevProposalDecisions BEGIN SELECT RAISE(ABORT, 'fixture audit failure'); END");
        }
        var engine = new ColumnProcessingEngine(projects, tickets, processors, executions, dispatcher,
            new ColumnActionExecutor(projects, tickets, NullLogger<ColumnActionExecutor>.Instance),
            new ColumnMemoryCapitalizationService(projects), NullLogger<ColumnProcessingEngine>.Instance,
            jev: qualification, jevConfiguration: configuration);
        await engine.ProcessAsync(project.Slug, processor, execution, CancellationToken.None);
        var updated = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        var fallback = scenario is "uncertain" or "none" or "api-error" or "missing-key";
        Assert.Equal(moves ? chosen.Id : fallback ? original.Id : source.Id, updated.ColumnId);
        var decisions = await new JevDecisionStore(projects).ListAsync(project.Slug, ticket.Id);
        if (moves)
        {
            var decision = Assert.Single(decisions);
            Assert.True(decision.Automatic);
            Assert.Equal(chosen.Id, decision.AppliedTargetColumnId);
            Assert.Equal(ColumnExecutionStatus.Completed, Assert.Single(await executions.ListAsync(project.Slug, ticket.Id)).Status);
            Assert.Contains(updated.Comments, c => c.Content == "Action after routing");
        }
        else
        {
            Assert.Empty(decisions);
            if (fallback)
            {
                Assert.Equal(ColumnExecutionStatus.Completed, Assert.Single(await executions.ListAsync(project.Slug, ticket.Id)).Status);
                Assert.Contains(updated.Comments, c => c.Content == "Action after routing");
            }
            else Assert.Empty(updated.Comments);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    private sealed class Dispatcher : IColumnAgentDispatcher
    {
        public int Calls;
        public Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ColumnDispatchResult(new AgentRun { RunId = execution.Id, ProjectSlug = slug,
                TicketId = ticket.Id, AgentName = processor.Name, SkillFile = "", ConcurrencyGroup = "test", StartedAt = DateTime.UtcNow },
                new("original", [], "Agent finished its work",
                    Evidence: processor.EvaluateAcceptanceCriteria ? new(ticket.UpdatedAt) : null,
                    AcceptanceCriteria: processor.EvaluateAcceptanceCriteria ? [new(ticket.AcceptanceCriteria[0].Id, AcceptanceStatus.PASS, new("All checks passed"))] : null), null));
        }
    }
}
