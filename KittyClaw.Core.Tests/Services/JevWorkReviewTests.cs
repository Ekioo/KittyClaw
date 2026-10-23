using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Evidence;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class JevWorkReviewTests
{
    [Theory]
    [InlineData(false, "insufficient")]
    [InlineData(true, "insufficient")]
    [InlineData(false, "uncertain")]
    [InlineData(true, "uncertain")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "missing-key")]
    [InlineData(true, "missing-key")]
    [InlineData(false, "budget")]
    [InlineData(true, "budget")]
    public async Task Acceptance_fallback_is_explicit_and_covers_uncertainty_and_service_failures(bool fallback, string scenario)
    {
        using var f = await Fixture.CreateAsync(configured: scenario != "missing-key");
        f.Processor = await f.Processors.SaveAsync(f.Slug, f.Source.Id, "Reviewer", "Validate work", null,
            true, 20, [], [], [], defaultTargetColumnId: f.Target.Id,
            jevAcceptanceValidation: true, jevFallbackToAgent: fallback);
        if (scenario != "insufficient") f.AddEvidence();
        f.Handler.Confidence = scenario == "uncertain" ? .2 : 1;
        f.Handler.Fail = scenario is "unavailable" or "budget";
        f.Handler.FailureStatus = scenario == "budget" ? HttpStatusCode.PaymentRequired : HttpStatusCode.ServiceUnavailable;
        await f.ObserveAsync();
        Assert.False(JevCertainty.Work(await f.ReviewAsync(), f.Processor.JevConfidenceThreshold));
        var reloaded = (await f.Processors.GetAsync(f.Slug, f.Source.Id))!;
        Assert.Equal(fallback, reloaded.JevFallbackToAgent);
        var executions = new ColumnExecutionService(f.Projects, f.Tickets, f.Reviews);
        Assert.Equal(fallback, await executions.UsesClassicFallbackAsync(f.Slug, reloaded, f.Ticket));
        var claim = await executions.ClaimNextAsync(f.Slug, reloaded, DateTime.UtcNow);
        if (fallback) Assert.True(Assert.IsType<ColumnExecution>(claim).JevClassicFallback);
        else
        {
            Assert.Null(claim);
            Assert.Empty(await executions.ListAsync(f.Slug, f.Ticket.Id));
            Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        }
    }

    [Fact]
    public async Task Uncertain_acceptance_does_not_block_a_classic_manual_transition()
    {
        using var f = await Fixture.CreateAsync();
        await f.ObserveAsync();
        var tickets = new TicketService(f.Projects, new MemberService(f.Projects), f.Processors,
            jevReviews: new Lazy<JevWorkReviewService>(() => f.Reviews));
        await tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, columnId: f.Corrections.Id);
        Assert.Equal(f.Corrections.Id, (await tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Empty(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id));
    }
    [Theory]
    [InlineData(false, "insufficient")]
    [InlineData(false, "uncertain")]
    [InlineData(false, "error")]
    [InlineData(true, "none")]
    [InlineData(true, "uncertain")]
    [InlineData(true, "error")]
    public async Task Uncertain_work_review_falls_back_to_column_defaults_without_approval(bool profile, string scenario)
    {
        using var f = await Fixture.CreateAsync(profile);
        if (!profile && scenario != "insufficient") f.AddEvidence();
        f.Handler.Confidence = scenario == "uncertain" ? .2 : 1;
        f.Handler.Abstain = scenario == "none";
        f.Handler.Fail = scenario == "error";
        await f.ObserveAsync();
        Assert.False(JevCertainty.Work(await f.ReviewAsync(), f.Processor.JevConfidenceThreshold));
        var executions = new ColumnExecutionService(f.Projects, f.Tickets, f.Reviews);
        var execution = await executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow);
        Assert.NotNull(execution);
        Assert.True(execution.JevClassicFallback);
        Assert.Null(execution.JevExecutionProfileJson);
        var effective = f.Processor.WithoutJev();
        Assert.False(effective.JevAcceptanceValidation);
        Assert.False(effective.JevExecutionSelection);
        Assert.False(effective.UsesAutomaticJevRouting);
        Assert.Equal(f.Processor.Model, effective.Model);
        Assert.Equal(f.Processor.RequiredSkills, effective.RequiredSkills);
        Assert.Equal(f.Processor.DefaultTargetColumnId, effective.DefaultTargetColumnId);
        Assert.Empty(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id));
        Assert.True(Assert.Single(await executions.ListAsync(f.Slug, f.Ticket.Id)).JevClassicFallback);
        var engine = new ColumnProcessingEngine(f.Projects, f.Tickets, f.Processors, executions, new ClassicDispatcher(),
            new ColumnActionExecutor(f.Projects, f.Tickets, Microsoft.Extensions.Logging.Abstractions.NullLogger<ColumnActionExecutor>.Instance),
            new ColumnMemoryCapitalizationService(f.Projects), Microsoft.Extensions.Logging.Abstractions.NullLogger<ColumnProcessingEngine>.Instance);
        await engine.ProcessAsync(f.Slug, f.Processor, execution, CancellationToken.None);
        Assert.Equal(f.Target.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Equal(ColumnExecutionStatus.Completed, Assert.Single(await executions.ListAsync(f.Slug, f.Ticket.Id)).Status);
    }
    [Fact]
    public async Task Acceptance_reviews_real_references_and_requires_owner_approval_before_any_move()
    {
        using var f = await Fixture.CreateAsync(); f.AddEvidence();
        await f.ObserveAsync();
        var review = await f.ReviewAsync();
        Assert.Null(review.Error); Assert.Equal(2, review.Criteria.Count);
        Assert.All(review.Criteria, c => { Assert.Equal("satisfied", c.Status); Assert.Contains(review.Evidence, e => e.Id == c.EvidenceId && e.Trust == EvidenceTrust.Verified); });
        Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        var executions = new ColumnExecutionService(f.Projects, f.Tickets, f.Reviews);
        Assert.Null(await executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, columnId: f.Target.Id));
        var signals = 0; f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        await Task.WhenAll(f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true),
            f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true));
        Assert.Equal(f.Target.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Single(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id)); Assert.Equal(1, signals);
    }

    [Theory]
    [InlineData(EvidenceTrust.AgentClaim, false)]
    [InlineData(EvidenceTrust.Verified, true)]
    public async Task Claims_stale_evidence_and_checked_boxes_never_prove_completion(EvidenceTrust trust, bool stale)
    {
        using var f = await Fixture.CreateAsync(); f.AddEvidence(trust, stale);
        await f.ObserveAsync(); var review = await f.ReviewAsync();
        Assert.Null(review.Error); Assert.All(review.Criteria, c => Assert.Equal("insufficient", c.Status));
        Assert.Equal(0, f.Handler.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true));
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true, "Owner independently verified these criteria");
        Assert.Equal(f.Target.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Contains("independently verified", Assert.Single(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id)).Reason);
    }

    [Fact]
    public async Task Rejection_routes_to_corrections_with_missing_criteria_in_the_owner_feedback()
    {
        using var f = await Fixture.CreateAsync(); f.AddEvidence(); f.Handler.Status = "unmet";
        await f.ObserveAsync(); var review = await f.ReviewAsync();
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, false, "Repair before submitting again");
        var ticket = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!;
        Assert.Equal(f.Corrections.Id, ticket.ColumnId);
        Assert.Contains(ticket.Comments, c => c.Author == "owner" && c.Content.Contains("Login works") && c.Content.Contains("Tests pass") && c.Content.Contains("Repair before"));
        Assert.False(Assert.Single(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id)).Accepted);
    }

    [Fact]
    public async Task Missing_reference_downgrades_satisfied_and_invented_reference_invalidates_the_whole_review()
    {
        using var f = await Fixture.CreateAsync(); f.AddEvidence(); f.Handler.Reference = "none";
        await f.ObserveAsync();
        Assert.All((await f.ReviewAsync()).Criteria, c => Assert.Equal("insufficient", c.Status));
        await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, description: "- [ ] Tests pass");
        f.Ticket = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!; f.Handler.Reference = "invented";
        await f.ObserveAsync(); var invalid = await f.ReviewAsync();
        Assert.NotNull(invalid.Error); Assert.Empty(invalid.Criteria);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, invalid.Id, true, "Override"));
    }

    [Fact]
    public async Task Changed_ticket_evidence_or_configuration_invalidates_a_review_and_unchanged_reviews_are_deduplicated()
    {
        using var f = await Fixture.CreateAsync(); f.AddEvidence();
        await f.ObserveAsync(); var before = await f.ReviewAsync();
        await f.ObserveAsync(); Assert.Equal(1, f.Handler.Count);
        f.AddEvidence(EvidenceTrust.Verified, false, exitCode: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, before.Id, true));
        await f.ObserveAsync(); Assert.Equal(2, f.Handler.Count);
        var current = await f.ReviewAsync(); Assert.NotEqual(before.Id, current.Id);
        await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, title: "New request");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, current.Id, true));
        Assert.Empty(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_approval_and_move()
    {
        using var f = await Fixture.CreateAsync(); f.AddEvidence(); await f.ObserveAsync(); var review = await f.ReviewAsync();
        await using (var db = f.Projects.GetProjectDb(f.Slug))
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_work_decision BEFORE INSERT ON JevWorkDecisions BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END");
        var signals = 0; f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true));
        Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Empty(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id)); Assert.Equal(0, signals);
    }

    [Fact]
    public async Task Model_and_skills_selection_holds_dispatch_until_approval_and_pins_a_per_execution_profile()
    {
        using var f = await Fixture.CreateAsync(profile: true); await f.ObserveAsync(); var review = await f.ReviewAsync();
        Assert.NotNull(review.Profile); Assert.Equal("Fast", review.Profile.Name);
        var executions = new ColumnExecutionService(f.Projects, f.Tickets, f.Reviews);
        Assert.Null(await executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow));
        var before = f.Ticket.UpdatedAt;
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true);
        Assert.Equal(before, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.UpdatedAt);
        var execution = await executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow);
        Assert.NotNull(execution);
        var selected = JsonSerializer.Deserialize<JevExecutionProfile>(execution.JevExecutionProfileJson!)!;
        Assert.Equal(review.Profile.Model, selected.Model); Assert.Equal(review.Profile.Skills, selected.Skills);
        Assert.Null(f.Processor.Model);
        Assert.True((await f.Processors.GetAsync(f.Slug, f.Source.Id))!.JevExecutionSelection);
    }

    [Fact]
    public async Task Rejected_profile_is_excluded_from_the_next_request_and_owner_feedback_is_sent()
    {
        using var f = await Fixture.CreateAsync(profile: true); await f.ObserveAsync(); var review = await f.ReviewAsync();
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, false, "Needs deeper reasoning");
        await f.ObserveAsync(); var alternative = await f.ReviewAsync();
        Assert.Equal("Deep", alternative.Profile!.Name);
        Assert.Contains("Needs deeper reasoning", f.Handler.State.GetProperty("ownerFeedback")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain("Fast", f.Handler.Options);
        Assert.Single(await f.Reviews.DecisionsAsync(f.Slug, f.Ticket.Id));
    }

    [Fact]
    public async Task Options_default_off_preserve_older_clients_and_restrict_profiles_to_authorized_skills()
    {
        using var f = await Fixture.CreateAsync(profile: true);
        var saved = await f.Processors.SaveAsync(f.Slug, f.Source.Id, "Worker", "Work", null, true, 20,
            f.Processor.AvailableSkills, [], f.Processor.RequiredSkills);
        Assert.True(saved.JevExecutionSelection); Assert.Equal(2, saved.JevExecutionProfiles.Count);
        var other = await f.Processors.SaveAsync(f.Slug, f.Target.Id, "Other", "Work", null, false, 20, [], [], []);
        Assert.False(other.JevExecutionSelection); Assert.False(other.JevAcceptanceValidation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Processors.SaveAsync(f.Slug, f.Source.Id, "Worker", "Work", null, true, 20, [], [], [],
            jevExecutionProfiles: [new("Unsafe", "claude-sonnet-4-6", ["invented-skill"])]));
    }

    [Fact]
    public async Task Ticket_changes_revoke_profile_approval_and_active_runs_do_not_trigger_more_inference()
    {
        using var f = await Fixture.CreateAsync(profile: true); await f.ObserveAsync(); var review = await f.ReviewAsync();
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true);
        await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, title: "A changed request");
        var executions = new ColumnExecutionService(f.Projects, f.Tickets, f.Reviews);
        Assert.Null(await executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow));
        f.Ticket = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!;
        await f.ObserveAsync(); review = await f.ReviewAsync();
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, f.Source.Id, review.Id, true);
        Assert.NotNull(await executions.ClaimNextAsync(f.Slug, f.Processor, DateTime.UtcNow));
        var calls = f.Handler.Count;
        await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, author: "worker", description: "Agent progress");
        f.Ticket = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!;
        await f.ObserveAsync(); Assert.Equal(calls, f.Handler.Count);
    }

    [Fact]
    public async Task Empty_criteria_are_explicitly_missing_and_ticket_deletion_removes_review_data()
    {
        using var f = await Fixture.CreateAsync();
        await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, description: "No explicit acceptance checklist yet");
        f.Ticket = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!;
        await f.ObserveAsync(); Assert.Contains("Define acceptance criteria", (await f.ReviewAsync()).Error);
        Assert.Equal(0, f.Handler.Count);
        await f.Tickets.DeleteTicketAsync(f.Slug, f.Ticket.Id);
        Assert.Null(await f.Reviews.GetAsync(f.Slug, f.Ticket.Id, f.Source.Id));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _temp = new();
        private HttpClient _client = null!;
        public ProjectService Projects = null!;
        public TicketService Tickets = null!;
        public ColumnProcessorService Processors = null!;
        public ColumnProcessor Processor = null!;
        public JevWorkReviewService Reviews = null!;
        public JevWorkDecisionService Decisions = null!;
        public Ticket Ticket = null!;
        public BoardColumn Source = null!, Target = null!, Corrections = null!;
        public string Slug = "";
        public Handler Handler = new();
        public static async Task<Fixture> CreateAsync(bool profile = false, bool configured = true)
        {
            var f = new Fixture(); f.Projects = new(f._temp.Path); f.Slug = (await f.Projects.CreateProjectAsync("JEV work review")).Slug;
            var columns = await new ColumnService(f.Projects).ListColumnsAsync(f.Slug);
            f.Source = columns.Single(c => c.Name == "Review"); f.Target = columns.Single(c => c.Name == "Done"); f.Corrections = columns.Single(c => c.Name == "Todo");
            var skills = new ProjectSkillService(f.Projects);
            var required = await skills.CreateAsync(f.Slug, "Required checks", "Run mandatory checks.");
            var optional = await skills.CreateAsync(f.Slug, "Testing", "Verify behavior.");
            f.Processors = new(f.Projects, skills); f.Tickets = new(f.Projects, new MemberService(f.Projects), f.Processors);
            f.Processor = await f.Processors.SaveAsync(f.Slug, f.Source.Id, "Reviewer", "Validate work", null, true, 20, [optional.Slug], [], [required.Slug],
                defaultTargetColumnId: f.Target.Id, routes: [new("changes_requested", f.Corrections.Id)],
                jevAcceptanceValidation: !profile, jevExecutionSelection: profile,
                jevExecutionProfiles: profile ? [new("Fast", "claude-haiku-4-5", [optional.Slug], "Simple work"), new("Deep", "claude-sonnet-4-6", [optional.Slug], "Complex work")] : []);
            f.Ticket = await f.Tickets.CreateTicketAsync(f.Slug, "Verify login", description: "- [x] Login works\n- [ ] Tests pass", columnId: f.Source.Id, pipelineId: f.Source.PipelineId);
            var vault = new ProjectSecretVault(f._temp.Path, new TestSecretProtector());
            if (configured) await vault.SetAsync(f.Slug, JevQualificationService.SecretName, "test-key");
            f._client = new(f.Handler);
            f.Reviews = new(f.Projects, new(vault, f._temp.Path, environmentKey: () => null, protector: new TestSecretProtector()), f._client);
            f.Decisions = new(f.Projects, f.Tickets, f.Processors, f.Reviews);
            return f;
        }
        public void AddEvidence(EvidenceTrust trust = EvidenceTrust.Verified, bool stale = false, int exitCode = 0)
        {
            var at = stale ? DateTime.UtcNow.AddDays(-2) : DateTime.UtcNow;
            new EvidenceStore(Projects.DataDir).SaveTicket(new()
            {
                TicketId = Ticket.Id.ToString(), ProjectSlug = Slug, CapturedAt = at, Status = EvidenceStatus.Complete, RunIds = ["proof"],
                CommandsRun = [new("tests", null, exitCode, exitCode == 0, null, exitCode == 0 ? 4 : 0, exitCode == 0 ? 0 : 1,
                    new("process-exit", "unknown", "proof", at, trust))]
            });
        }
        public Task ObserveAsync() => Reviews.ObserveAsync(Slug, Processor, Ticket, CancellationToken.None);
        public async Task<JevWorkReview> ReviewAsync() => (await Reviews.GetAsync(Slug, Ticket.Id, Source.Id))!;
        public void Dispose() { _client.Dispose(); _temp.Dispose(); }
    }

    private sealed class ClassicDispatcher : IColumnAgentDispatcher
    {
        public Task<ColumnDispatchResult> DispatchAsync(string slug, ColumnProcessor processor, ColumnExecution execution, Ticket ticket, CancellationToken ct)
        {
            Assert.False(processor.JevAcceptanceValidation);
            Assert.False(processor.JevExecutionSelection);
            Assert.True(processor.JevClassicFallback);
            return Task.FromResult(new ColumnDispatchResult(new AgentRun { RunId = execution.Id, ProjectSlug = slug, TicketId = ticket.Id,
                AgentName = processor.Name, SkillFile = "", ConcurrencyGroup = "fixture", StartedAt = DateTime.UtcNow },
                new ColumnAgentResult("completed", processor.RequiredSkills, "Classic processor completed", Evidence: new(ticket.UpdatedAt)), null));
        }
    }

    internal sealed class Handler : HttpMessageHandler
    {
        public double Confidence = 1;
        public bool Abstain, Fail;
        public HttpStatusCode FailureStatus = HttpStatusCode.ServiceUnavailable;
        public int Count;
        public string Status = "satisfied";
        public string? Reference;
        public JsonElement State;
        public string Options = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) return new(FailureStatus);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            State = body.RootElement.GetProperty("state").Clone();
            var answers = new Dictionary<string, object>();
            foreach (var question in body.RootElement.GetProperty("questions").EnumerateObject())
            {
                var keys = question.Value.GetProperty("criteria").EnumerateObject().Select(p => p.Name).ToList();
                var choice = question.Name.StartsWith("criterion-") ? Status : question.Name.StartsWith("evidence-") ? Reference ?? keys.First(k => k != "none") : keys.First(k => k != "none");
                Options = question.Value.GetProperty("criteria").ToString();
                if (Abstain) choice = "none";
                answers[question.Name] = new { type = "choice", choice, confidence = Confidence, probabilities = keys.ToDictionary(k => k, k => k == choice ? 1.0 : 0.0) };
            }
            Count++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { model = "jev-1.14.0", answers, usage = new { input_tokens = 1000, output_tokens = 40 } }) };
        }
    }
}
