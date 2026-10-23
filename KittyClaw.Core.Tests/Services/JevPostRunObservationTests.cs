using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class JevPostRunObservationTests
{
    [Fact]
    public async Task Completed_run_proposal_is_read_only_durable_and_refreshes_once_when_evidence_arrives()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Post run");
        var columns = await new ColumnService(projects).ListColumnsAsync(project.Slug);
        var source = columns[0]; var target = columns[1];
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(project.Slug, source.Id, "Worker", "Implement and review", null,
            true, 20, [], [], [], defaultTargetColumnId: target.Id, jevPostRunObservation: true);
        Assert.True((await processors.GetAsync(project.Slug, source.Id))!.JevPostRunObservation);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Completed work", status: target.Name,
            pipelineId: target.PipelineId, columnId: target.Id);
        var execution = new ColumnExecution { Id = "run-one", RunId = "run-one", TicketId = ticket.Id,
            ProcessorId = processor.Id, Status = ColumnExecutionStatus.Completed, EndedAt = DateTime.UtcNow,
            TargetColumnId = target.Id, Outcome = "completed", AgentResult = new("completed", [], "Agent says all tests passed.") };
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(project.Slug, JevQualificationService.SecretName, "fake-key");
        var handler = new PostRunHandler();
        using var client = new HttpClient(handler);
        var service = new JevQualificationService(projects, vault, client);
        await service.ObserveAsync(project.Slug, processor, ticket, columns, CancellationToken.None, execution);
        var proposal = Assert.Single(await service.GetPostRunAsync(project.Slug, ticket.Id));
        Assert.Equal(execution.Id, proposal.ExecutionId);
        Assert.Null(proposal.Error);
        Assert.Null(proposal.Triage);
        Assert.False(proposal.EvidenceAvailable);
        Assert.Null(await service.GetAsync(project.Slug, ticket.Id));
        Assert.Equal(target.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
        Assert.Equal(ticket.UpdatedAt, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.UpdatedAt);
        Assert.Equal("Agent says all tests passed.", handler.LastState.GetProperty("execution").GetProperty("agentClaims").GetProperty("summary").GetString());
        Assert.False(handler.LastState.GetProperty("evidenceAvailable").GetBoolean());
        var store = new EvidenceStore(projects.DataDir);
        store.SaveRun(new TicketEvidence
        {
            TicketId = ticket.Id.ToString(), ProjectSlug = project.Slug, CapturedAt = DateTime.UtcNow,
            Status = EvidenceStatus.Complete, RunIds = [execution.RunId],
            CommandsRun = [new("tests", null, 0, true, null, 4, 0,
                new("process-exit", "codex", execution.RunId, DateTime.UtcNow, EvidenceTrust.Verified))]
        });
        await service.ObserveAsync(project.Slug, processor, ticket, columns, CancellationToken.None, execution);
        Assert.True(Assert.Single(await service.GetPostRunAsync(project.Slug, ticket.Id)).EvidenceAvailable);
        Assert.Equal("Verified", handler.LastState.GetProperty("evidence").GetProperty("commands")[0].GetProperty("provenance").GetProperty("trust").GetString());
        Assert.False(handler.LastState.GetProperty("evidence").GetProperty("commands")[0].TryGetProperty("command", out _));
        var restarted = new JevQualificationService(projects, vault, client);
        ticket.Description = "Later unrelated ticket edit";
        await restarted.ObserveAsync(project.Slug, processor, ticket, columns, CancellationToken.None, execution);
        Assert.Equal(2, handler.Count);
        // A manual retry may reuse the execution/run id: a new terminal snapshot must remain distinct.
        execution.EndedAt = execution.EndedAt!.Value.AddTicks(1);
        await restarted.ObserveAsync(project.Slug, processor, ticket, columns, CancellationToken.None, execution);
        Assert.Equal(2, (await restarted.GetPostRunAsync(project.Slug, ticket.Id)).Count);
        Assert.Equal(3, handler.Count);
        // Running, pre-opt-in and disabled executions cannot trigger API calls.
        execution.Id = "running"; execution.Status = ColumnExecutionStatus.Running;
        await restarted.ObserveAsync(project.Slug, processor, ticket, columns, CancellationToken.None, execution);
        execution.Status = ColumnExecutionStatus.Completed; execution.EndedAt = processor.JevPostRunObservationSince!.Value.AddSeconds(-1);
        await restarted.ObserveAsync(project.Slug, processor, ticket, columns, CancellationToken.None, execution);
        execution.EndedAt = DateTime.UtcNow; processor.JevPostRunObservationSince = null;
        await restarted.ObserveAsync(project.Slug, processor, ticket, columns, CancellationToken.None, execution);
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task Post_run_opt_in_is_preserved_by_older_clients_and_cannot_replace_qualification_mode()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Configuration");
        var column = (await new ColumnService(projects).ListColumnsAsync(project.Slug))[0];
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var first = await processors.SaveAsync(project.Slug, column.Id, "Worker", "Work", null, true, 20, [], [], [], jevPostRunObservation: true);
        var saved = await processors.SaveAsync(project.Slug, column.Id, "Worker", "Work", null, true, 20, [], [], []);
        Assert.Equal(first.JevPostRunObservationSince, saved.JevPostRunObservationSince);
        Assert.False(saved.JevObservationOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processors.SaveAsync(project.Slug, column.Id,
            "Worker", "Work", null, true, 20, [], [], [], jevObservationOnly: true, jevPostRunObservation: true));
        var disabled = await processors.SaveAsync(project.Slug, column.Id, "Worker", "Work", null, true, 20, [], [], [], jevPostRunObservation: false);
        Assert.False(disabled.JevPostRunObservation);
    }

    internal sealed class PostRunHandler : HttpMessageHandler
    {
        public int Count;
        public double Confidence = 1;
        public JsonElement LastState;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            LastState = body.RootElement.GetProperty("state").Clone();
            var questions = body.RootElement.GetProperty("questions");
            var options = questions.GetProperty("routing").GetProperty("criteria").EnumerateObject().Select(p => p.Name).ToList();
            var selected = options.First(k => k != "none");
            // The ticket panel also asks for a current-column recommendation independently of post-run history.
            if (questions.EnumerateObject().Count() > 1)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                    model = "jev-owner-fixture", answers = KittyClaw.Core.Tests.Helpers.JevResponseFixture.Answers(selected) }) };
            Count++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { model = "jev-post-run-fixture",
                answers = new { routing = new { type = "choice", choice = selected, confidence = Confidence,
                    probabilities = options.ToDictionary(k => k, k => k == selected ? (Confidence * (options.Count - 1) + 1) / options.Count : (1 - Confidence) / options.Count) } } }) };
        }
    }
}
