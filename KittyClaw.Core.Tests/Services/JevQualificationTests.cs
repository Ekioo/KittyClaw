using System.Net;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class JevQualificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Comment_budget_preserves_essential_context_and_recent_complete_comments(bool postRun)
    {
        var context = new { ticket = new { title = "Goal", description = "Essential request",
            acceptanceCriteria = new[] { "Must pass" },
            comments = Enumerable.Range(0, 20).Select(i => new { author = "owner", content = $"{i}:" + new string('é', 2000) }) },
            mission = "Implement", destinations = new { none = "Insufficient evidence" } };
        object state = postRun ? new { context, execution = new { summary = new string('x', 6000) } } : context;
        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var compact = JevQualificationService.FitCommentContext(json);
        Assert.True(compact.ToJsonString().Length <= 48000);
        var ticket = (postRun ? compact["context"] : compact)!["ticket"]!;
        Assert.Equal("Essential request", ticket["description"]!.GetValue<string>());
        Assert.Equal("Must pass", ticket["acceptanceCriteria"]![0]!.GetValue<string>());
        Assert.NotNull(ticket["commentContextNotice"]);
        var comments = ticket["comments"]!.AsArray();
        Assert.InRange(comments.Count, 1, 19);
        Assert.Equal("19:" + new string('é', 2000), comments.Last()!["content"]!.GetValue<string>());
        var next = new { author = "owner", content = "extra:" + new string('é', 2000) };
        comments.Insert(0, System.Text.Json.JsonSerializer.SerializeToNode(next));
        Assert.True(compact.ToJsonString().Length > 48000);
    }

    [Fact]
    public void Oversized_comment_does_not_exclude_other_comments_and_small_context_is_unchanged()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { ticket = new { description = "Keep",
            comments = new[] { new { content = "Older but useful" }, new { content = new string('x', 60000) }, new { content = "Latest" } } } });
        var state = JevQualificationService.FitCommentContext(json);
        Assert.Equal(new[] { "Older but useful", "Latest" }, state["ticket"]!["comments"]!.AsArray()
            .Select(c => c!["content"]!.GetValue<string>()));
        const string small = "{\"ticket\":{\"comments\":[]}}";
        Assert.Equal(small, JevQualificationService.FitCommentContext(small).ToJsonString());
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.FitCommentContext(
            System.Text.Json.JsonSerializer.Serialize(new { ticket = new { description = new string('x', 50000), comments = Array.Empty<object>() } })));
    }

    [Fact]
    public async Task Worker_routing_does_not_request_Jev_before_the_agent_result()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var slug = (await projects.CreateProjectAsync("Worker routing order")).Slug;
        var board = await new ColumnService(projects).ListColumnsAsync(slug);
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(slug, board[0].Id, "Worker", "Do the work", null,
            true, 20, [], [], [], defaultTargetColumnId: board[1].Id);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(slug, "Work first", columnId: board[0].Id, pipelineId: board[0].PipelineId);
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(slug, JevQualificationService.SecretName, "fixture-key");
        var handler = new FakeHandler(board[1].Id);
        using var client = new HttpClient(handler);
        var qualification = new JevQualificationService(projects, vault, client);
        await qualification.ObserveAsync(slug, processor, ticket, board, CancellationToken.None);
        // Opening the ticket uses a recommendation context and must obey the same guard.
        await qualification.ObserveAsync(slug,
            JevQualificationService.RecommendationProcessor(processor, board[0], board), ticket, board, CancellationToken.None);
        Assert.Equal(0, handler.Count);
        Assert.Null(await qualification.GetAsync(slug, ticket.Id));
    }

    [Fact]
    public async Task Qualification_without_a_usable_answer_claims_classic_processing_instead_of_holding()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var slug = (await projects.CreateProjectAsync("Qualification fallback")).Slug;
        var board = await new ColumnService(projects).ListColumnsAsync(slug);
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(slug, board[0].Id, "Qualifier", "Qualify tickets", null,
            true, 20, [], [], [], defaultTargetColumnId: board[1].Id, jevObservationOnly: true);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(slug, "Needs normal processing", columnId: board[0].Id, pipelineId: board[0].PipelineId);
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        var handler = new FakeHandler(board[1].Id);
        using var client = new HttpClient(handler);
        var qualification = new JevQualificationService(projects, vault, client,
            new JevConfigurationService(vault, temp.Path, () => null, new TestSecretProtector()));
        await qualification.ObserveAsync(slug, processor, ticket, board, CancellationToken.None);
        var executions = new ColumnExecutionService(projects, tickets, qualification: qualification);
        var execution = await executions.ClaimNextAsync(slug, processor, DateTime.UtcNow);
        Assert.NotNull(execution);
        Assert.True(execution.JevClassicFallback);
        Assert.False(processor.WithoutJev().JevObservationOnly);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task On_demand_recommendation_in_an_unconfigured_column_uses_only_allowed_manual_destinations()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var slug = (await projects.CreateProjectAsync("Owner recommendation")).Slug;
        var board = await new ColumnService(projects).ListColumnsAsync(slug);
        var source = board[0]; var target = board[1];
        var context = JevQualificationService.RecommendationProcessor(null, source, board);
        Assert.DoesNotContain(context.Routes, r => r.TargetColumnId == source.Id);
        Assert.Contains(context.Routes, r => r.TargetColumnId == target.Id);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(slug, "What should happen next?", columnId: source.Id, pipelineId: source.PipelineId);
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(slug, JevQualificationService.SecretName, "fixture-key");
        using var client = new HttpClient(new RecommendationHandler(target.Id));
        var qualification = new JevQualificationService(projects, vault, client);
        await qualification.ObserveAsync(slug, context, ticket, board, CancellationToken.None);
        var proposal = (await qualification.GetAsync(slug, ticket.Id))!;
        Assert.Null(proposal.Error);
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var decisions = new JevProposalDecisionService(projects, tickets, processors, new JevDecisionStore(projects));
        Assert.Null(await decisions.TryApplyAutomaticallyAsync(slug, ticket.Id, JevDecisionStore.ProposalId(proposal)));
        await decisions.DecideAsync(slug, ticket.Id, JevDecisionStore.ProposalId(proposal), true);
        Assert.Equal(target.Id, (await tickets.GetTicketAsync(slug, ticket.Id))!.ColumnId);
        Assert.Empty(await processors.ListAsync(slug));
    }

    private sealed class RecommendationHandler(int target) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var options = body["questions"]!["routing"]!["criteria"]!.AsObject().Select(p => p.Key);
            var answers = System.Text.Json.JsonSerializer.SerializeToNode(JevResponseFixture.Answers($"column-{target}"))!;
            answers["routing"]!["probabilities"] = System.Text.Json.JsonSerializer.SerializeToNode(options.ToDictionary(k => k, k => k == $"column-{target}" ? 1.0 : 0.0));
            return new(HttpStatusCode.OK) { Content = new StringContent(new System.Text.Json.Nodes.JsonObject { ["model"] = "jev-fixture", ["answers"] = answers }.ToJsonString()) };
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observation_persists_without_mutation_and_deduplicates_after_restart(bool invalidTriage)
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Jev test");
        var columns = new ColumnService(projects);
        var board = await columns.ListColumnsAsync(project.Slug);
        var source = board[0]; var target = board[1];
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(project.Slug, source.Id, "Qualification", "Qualify tickets", null,
            true, 20, [], [], [], defaultTargetColumnId: target.Id, jevObservationOnly: true);
        Assert.True((await processors.GetAsync(project.Slug, source.Id))!.JevObservationOnly);
        // Old clients omitting the new field must preserve observation mode.
        processor = await processors.SaveAsync(project.Slug, source.Id, "Qualification", "Qualify tickets", null,
            true, 20, [], [], [], defaultTargetColumnId: target.Id);
        Assert.True(processor.JevObservationOnly);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Fix a bug", status: source.Name,
            pipelineId: source.PipelineId, columnId: source.Id);
        var executions = new ColumnExecutionService(projects, tickets);
        Assert.Null(await executions.ClaimNextAsync(project.Slug, processor, DateTime.UtcNow));
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(project.Slug, JevQualificationService.SecretName, "test-secret");
        var handler = new FakeHandler(target.Id, invalidTriage);
        ticket.Comments = Enumerable.Range(0, 20).Select(i => new KittyClaw.Core.Models.Comment
            { Content = $"Comment {i}:" + new string('x', 8000) }).ToList();
        using var client = new HttpClient(handler);
        var service = new JevQualificationService(projects, vault, client);
        await service.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        var proposal = await service.GetAsync(project.Slug, ticket.Id);
        if (invalidTriage)
        {
            Assert.NotNull(proposal!.Error);
            Assert.Null(proposal.Choice);
            Assert.Null(proposal.Triage);
            Assert.Equal(source.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
            await service.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
            Assert.Equal(1, handler.Count);
            return;
        }
        Assert.Null(proposal!.Error);
        Assert.Equal($"column-{target.Id}", proposal.Choice);
        Assert.Equal("problem", proposal.Triage!.RequestType.Choice);
        Assert.Equal("moderate", proposal.Triage.Complexity.Choice);
        Assert.Equal(0.15, proposal.Triage.ClarificationProbability);
        Assert.Equal(100, proposal.Usage!.InputTokens);
        Assert.NotNull(proposal.DurationMs);
        Assert.Equal("qualification-v3", proposal.QuestionVersion);
        Assert.Equal(source.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
        Assert.Equal(ticket.UpdatedAt, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.UpdatedAt);
        var restarted = new JevQualificationService(projects, vault, client);
        await restarted.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        Assert.Equal(1, handler.Count);
        processor.JevObservationOnly = false;
        ticket.Description = "Changed";
        await restarted.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Missing_credentials_are_visible_and_do_not_make_http_calls()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Missing key");
        var board = await new ColumnService(projects).ListColumnsAsync(project.Slug);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Qualify", status: board[0].Name,
            pipelineId: board[0].PipelineId, columnId: board[0].Id);
        var processor = new ColumnProcessor { Name = "Qualifier", ColumnId = board[0].Id,
            DefaultTargetColumnId = board[1].Id, JevObservationOnly = true };
        var handler = new FakeHandler(board[1].Id);
        using var client = new HttpClient(handler);
        var service = new JevQualificationService(projects,
            new ProjectSecretVault(temp.Path, new TestSecretProtector()), client,
            new JevConfigurationService(new ProjectSecretVault(temp.Path, new TestSecretProtector()), temp.Path, () => null, new TestSecretProtector()));
        await service.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        var proposal = await service.GetAsync(project.Slug, ticket.Id);
        Assert.Contains("JEV is unavailable", proposal!.Error);
        Assert.Null(proposal.Choice);
        Assert.Equal(0, handler.Count);
        Assert.Equal(board[0].Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ColumnId);
    }

    [Fact]
    public void Invalid_or_unknown_destinations_are_rejected()
    {
        var destinations = new Dictionary<string, string> { ["none"] = "No match", ["column-2"] = "Review" };
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.ValidateAnswer(destinations,
            "column-999", 1, new() { ["none"] = 0, ["column-2"] = 1 }));
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.ValidateAnswer(destinations,
            "none", double.NaN, new() { ["none"] = 1, ["column-2"] = 0 }));
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.ValidateAnswer(destinations,
            "none", 1, new() { ["none"] = 1 }));
    }

    [Fact]
    public void Failure_routes_are_not_business_routing_candidates()
    {
        var processor = new ColumnProcessor { Name = "Test", ColumnId = 1, TechnicalFailureColumnId = 3,
            DefaultTargetColumnId = 2, Routes = [new("needs-review", 2)] };
        var options = JevQualificationService.BuildDestinations(processor,
            [new() { Id = 2, Name = "Review" }, new() { Id = 3, Name = "Failed" }]);
        Assert.Equal(2, options.Count);
        Assert.Contains("needs-review", options["column-2"]);
        Assert.Contains("none", options.Keys);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Invalid_clarification_probabilities_are_rejected(double probability)
    {
        using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { type = "noul", noul = probability }));
        Assert.Throws<InvalidOperationException>(() => JevQualificationService.ReadNoul(body.RootElement));
    }

    [Fact]
    public void Legacy_routing_observations_remain_readable_without_inventing_triage()
    {
        var legacy = new JevRoutingProposal("old", 1, DateTime.UtcNow, "old-model", "none", 1,
            new() { ["none"] = 1 }, new() { ["none"] = "No match" }, null);
        var node = System.Text.Json.JsonSerializer.SerializeToNode(legacy)!.AsObject();
        foreach (var field in new[] { "Triage", "Usage", "DurationMs", "QuestionVersion" }) node.Remove(field);
        var json = node.ToJsonString();
        var restored = System.Text.Json.JsonSerializer.Deserialize<JevRoutingProposal>(json)!;
        Assert.Null(restored.Triage);
        Assert.Null(restored.Usage);
        Assert.Equal("routing-v1", restored.QuestionVersion);
    }

    private sealed class FakeHandler(int target, bool invalidTriage = false) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.True(body.RootElement.GetProperty("state").GetRawText().Length <= 48000);
            Assert.Equal(4, body.RootElement.GetProperty("questions").EnumerateObject().Count());
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = "jev-test", answers = JevResponseFixture.Answers($"column-{target}", invalidTriage ? 1.5 : 0.15),
                usage = new { input_tokens = 100, output_tokens = 20 }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }
}
