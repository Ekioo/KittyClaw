using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class JevProposalDecisionTests
{
    [Fact]
    public async Task Acceptance_applies_normal_transition_atomically_and_duplicate_clicks_do_not_replay_it()
    {
        using var f = await Fixture.CreateAsync();
        var proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        var id = JevDecisionStore.ProposalId(proposal);
        var signals = 0;
        f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        var results = await Task.WhenAll(f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, true, "Approved"),
            f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, true, "Approved"));
        Assert.Equal(results[0].ProposalId, results[1].ProposalId);
        Assert.Equal(f.First.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Equal(1, signals);
        var trace = Assert.Single(await f.Store.ListAsync(f.Slug, f.Ticket.Id));
        Assert.True(trace.Accepted);
        Assert.Equal("owner", trace.Actor);
        Assert.Equal("Approved", trace.Reason);
        Assert.Equal(id, JevDecisionStore.ProposalId(trace.Proposal));
        Assert.Equal(f.First.Id, trace.AppliedTargetColumnId);
        var restarted = new JevDecisionStore(f.Projects);
        Assert.Equal(id, Assert.Single(await restarted.ListAsync(f.Slug, f.Ticket.Id)).ProposalId);
        Assert.Contains((await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.Activities, a => a.Text.Contains("JEV proposal") && a.Text.Contains("accepted"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, false, "Changed my mind"));
    }

    [Fact]
    public async Task Rejection_excludes_destination_and_supplies_owner_reason_to_next_qualification()
    {
        using var f = await Fixture.CreateAsync();
        var before = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        var signals = 0; f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        var decision = await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(before), false, "Needs independent verification");
        Assert.False(decision.Accepted);
        Assert.Equal(f.Ticket.UpdatedAt, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.UpdatedAt);
        Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Equal(0, signals);
        await f.Qualification.ObserveAsync(f.Slug, f.Processor, f.Ticket, f.Columns, CancellationToken.None);
        var after = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        Assert.Null(after.Error);
        Assert.Equal($"column-{f.Second.Id}", after.Choice);
        Assert.DoesNotContain($"column-{f.First.Id}", after.Destinations.Keys);
        Assert.Equal("Needs independent verification", f.Handler.LastState.GetProperty("ownerFeedback")[0].GetProperty("reason").GetString());
        Assert.Equal(2, f.Handler.Count);
        Assert.Equal(before.Choice, Assert.Single(await f.Store.ListAsync(f.Slug, f.Ticket.Id)).Proposal.Choice);
        Assert.NotEqual(JevDecisionStore.ProposalId(before), JevDecisionStore.ProposalId(after));
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(after), true);
        Assert.Equal(f.Second.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Equal(2, (await f.Store.ListAsync(f.Slug, f.Ticket.Id)).Count);
    }

    [Fact]
    public async Task Exhausted_destinations_stop_requalification_until_ticket_context_changes()
    {
        using var f = await Fixture.CreateAsync(alternatives: false);
        var proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(proposal), false, "Wrong destination");
        await f.Qualification.ObserveAsync(f.Slug, f.Processor, f.Ticket, f.Columns, CancellationToken.None);
        Assert.Equal(1, f.Handler.Count);
        await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, description: "A clarified task");
        var changed = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!;
        await f.Qualification.ObserveAsync(f.Slug, f.Processor, changed, f.Columns, CancellationToken.None);
        Assert.Equal(2, f.Handler.Count);
        Assert.Single(await f.Store.ListAsync(f.Slug, f.Ticket.Id));
    }

    [Fact]
    public async Task Stale_tickets_changed_proposals_and_empty_rejections_cannot_be_applied()
    {
        using var f = await Fixture.CreateAsync();
        var proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        var id = JevDecisionStore.ProposalId(proposal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, false, "  "));
        await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, description: "New requirements");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, true));
        var changed = (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!;
        await f.Qualification.ObserveAsync(f.Slug, f.Processor, changed, f.Columns, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, true));
        Assert.Empty(await f.Store.ListAsync(f.Slug, f.Ticket.Id));
        Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_failure_rolls_back_transition_and_emits_no_status_signal(bool automatic)
    {
        using var f = await Fixture.CreateAsync();
        if (automatic) await ConfigureAutomaticAsync(f, true, 0.9);
        var id = JevDecisionStore.ProposalId((await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!);
        await f.Store.ListAsync(f.Slug, f.Ticket.Id);
        await using (var db = f.Projects.GetProjectDb(f.Slug))
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_jev_review BEFORE INSERT ON JevProposalDecisions BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END");
        var signals = 0; f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
        {
            if (automatic) await f.Decisions.TryApplyAutomaticallyAsync(f.Slug, f.Ticket.Id, id);
            else await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, true);
        });
        Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Equal(0, signals);
        Assert.Empty(await f.Store.ListAsync(f.Slug, f.Ticket.Id));
        Assert.DoesNotContain((await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.Activities, a => a.Text.Contains("accepted"));
    }

    [Fact]
    public async Task Post_run_acceptance_respects_the_current_columns_manual_routing_policy()
    {
        using var f = await Fixture.CreateAsync(postRun: true);
        await f.Processors.SaveAsync(f.Slug, f.First.Id, "Review", "Review work", null, true, 20, [], [], [], defaultTargetColumnId: f.Source.Id);
        var current = (await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, columnId: f.First.Id))!;
        var executions = new ColumnExecutionService(f.Projects, f.Tickets);
        await executions.ListAsync(f.Slug);
        var execution = new ColumnExecution { Id = "post-run", RunId = "post-run", ProcessorId = f.Processor.Id,
            TicketId = current.Id, Status = ColumnExecutionStatus.Completed, EndedAt = DateTime.UtcNow, TargetColumnId = f.First.Id };
        await using (var db = f.Projects.GetProjectDb(f.Slug)) { db.ColumnExecutions.Add(execution); await db.SaveChangesAsync(); }
        await f.Qualification.ObserveAsync(f.Slug, f.Processor, current, f.Columns, CancellationToken.None, execution);
        var proposal = Assert.Single(await f.Qualification.GetPostRunAsync(f.Slug, current.Id));
        // Fixture selects the source's first route (the current column). Seed a different valid source route instead.
        var alternate = proposal with { Choice = $"column-{f.Second.Id}", Probabilities = proposal.Destinations.ToDictionary(p => p.Key, p => p.Key == $"column-{f.Second.Id}" ? 1.0 : 0.0) };
        await using (var db = f.Projects.GetProjectDb(f.Slug))
        {
            var payload = JsonSerializer.Serialize(alternate, JevDecisionStore.Json);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JevPostRunObservations SET Payload = {payload} WHERE TicketId = {current.Id}");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Decisions.DecideAsync(f.Slug, current.Id, JevDecisionStore.ProposalId(alternate), true));
        Assert.Equal(f.First.Id, (await f.Tickets.GetTicketAsync(f.Slug, current.Id))!.ColumnId);
        Assert.Empty(await f.Store.ListAsync(f.Slug, current.Id));
    }

    [Theory]
    [InlineData(false, 0.9, 1, false)]
    [InlineData(true, 0.91, 0.9, false)]
    [InlineData(true, 0.9, 0.9, true)]
    [InlineData(true, 1, 1, true)]
    [InlineData(true, 0, 0, true)]
    public async Task Automatic_routing_requires_opt_in_and_inclusive_confidence_threshold(bool enabled, double threshold, double confidence, bool moves)
    {
        using var f = await Fixture.CreateAsync();
        Assert.False(f.Processor.JevAutomaticRouting);
        Assert.Equal(0.9, f.Processor.JevConfidenceThreshold);
        await ConfigureAutomaticAsync(f, enabled, threshold);
        var proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        proposal = proposal with { Confidence = confidence,
            Probabilities = proposal.Destinations.Keys.ToDictionary(k => k, k => k == proposal.Choice
                ? (confidence * (proposal.Destinations.Count - 1) + 1) / proposal.Destinations.Count
                : (1 - confidence) / proposal.Destinations.Count) };
        await ReplaceProposalAsync(f, proposal);
        var signals = 0; f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        var result = await f.Decisions.TryApplyAutomaticallyAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(proposal));
        Assert.Equal(moves ? f.First.Id : f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Equal(moves ? 1 : 0, signals);
        if (moves)
        {
            Assert.NotNull(result);
            Assert.True(result.Automatic); Assert.Equal("JEV", result.Actor); Assert.Equal(threshold, result.ConfidenceThreshold);
            Assert.Contains((await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.Activities, a => a.Author == "JEV" && a.Text.Contains("Automatic routing"));
            Assert.Null(await f.Decisions.TryApplyAutomaticallyAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(proposal)));
            Assert.Single(await f.Store.ListAsync(f.Slug, f.Ticket.Id)); Assert.Equal(1, signals);
        }
        else
        {
            Assert.Null(result); Assert.Empty(await f.Store.ListAsync(f.Slug, f.Ticket.Id));
            // A threshold never prevents an explicit owner acceptance.
            Assert.False((await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(proposal), true)).Automatic);
        }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("none")]
    [InlineData("error")]
    [InlineData("rejected")]
    [InlineData("paused")]
    [InlineData("disabled")]
    [InlineData("context")]
    [InlineData("legacy")]
    [InlineData("active")]
    public async Task Automatic_routing_leaves_ineligible_proposals_for_manual_review(string scenario)
    {
        using var f = await Fixture.CreateAsync();
        await ConfigureAutomaticAsync(f, true, 0.9);
        var proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        if (scenario == "stale") await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, description: "New requirements");
        if (scenario == "none") proposal = proposal with { Choice = "none" };
        if (scenario == "error") proposal = proposal with { Error = "TypeSafe returned HTTP 500." };
        if (scenario == "legacy") proposal = proposal with { ProcessorFingerprint = null };
        await ReplaceProposalAsync(f, proposal);
        var id = JevDecisionStore.ProposalId(proposal);
        if (scenario == "rejected") await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, false, "Owner declines");
        if (scenario == "paused") await f.Projects.TogglePauseAsync(f.Slug);
        if (scenario == "disabled") await ConfigureAutomaticAsync(f, false, 0.9);
        if (scenario == "context") await f.Processors.SaveAsync(f.Slug, f.Source.Id, "Qualifier", "A changed mission", null, true, 20, [], [], [],
            routes: f.Processor.Routes, defaultTargetColumnId: f.Processor.DefaultTargetColumnId);
        if (scenario == "active")
        {
            var executions = new ColumnExecutionService(f.Projects, f.Tickets); await executions.ListAsync(f.Slug);
            await using var db = f.Projects.GetProjectDb(f.Slug);
            db.ColumnExecutions.Add(new() { Id = "active", ProcessorId = f.Processor.Id, TicketId = f.Ticket.Id, Status = ColumnExecutionStatus.Running });
            await db.SaveChangesAsync();
        }
        Assert.Null(await f.Decisions.TryApplyAutomaticallyAsync(f.Slug, f.Ticket.Id, id));
        Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.DoesNotContain(await f.Store.ListAsync(f.Slug, f.Ticket.Id), d => d.Automatic);
    }

    [Fact]
    public async Task Automatic_routing_does_not_repeat_a_recent_transition_after_the_ticket_returns()
    {
        using var f = await Fixture.CreateAsync(); await ConfigureAutomaticAsync(f, true, 0.9);
        var proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        Assert.NotNull(await f.Decisions.TryApplyAutomaticallyAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(proposal)));
        var returned = (await f.Tickets.UpdateTicketAsync(f.Slug, f.Ticket.Id, columnId: f.Source.Id))!;
        await f.Qualification.ObserveAsync(f.Slug, f.Processor, returned, f.Columns, CancellationToken.None);
        proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        Assert.Null(await f.Decisions.TryApplyAutomaticallyAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(proposal)));
        Assert.Equal(f.Source.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.True((await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, JevDecisionStore.ProposalId(proposal), true)).Accepted);
    }

    [Fact]
    public async Task Automatic_policy_persists_across_older_clients_and_rejects_invalid_thresholds()
    {
        using var f = await Fixture.CreateAsync(); await ConfigureAutomaticAsync(f, true, 0.95);
        var oldClientSave = await f.Processors.SaveAsync(f.Slug, f.Source.Id, "Qualifier", "Qualify work", null, true, 20, [], [], [],
            routes: f.Processor.Routes, defaultTargetColumnId: f.Processor.DefaultTargetColumnId);
        Assert.True(oldClientSave.JevAutomaticRouting); Assert.Equal(0.95, oldClientSave.JevConfidenceThreshold);
        var restarted = new ColumnProcessorService(f.Projects, new ProjectSkillService(f.Projects));
        Assert.True((await restarted.GetAsync(f.Slug, f.Source.Id))!.JevAutomaticRouting);
        Assert.False((await restarted.SaveAsync(f.Slug, f.Second.Id, "Other", "Work", null, false, 20, [], [], [])).JevAutomaticRouting);
        foreach (var invalid in new[] { -0.01, 1.01, double.NaN, double.PositiveInfinity })
            await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigureAutomaticAsync(f, true, invalid));
        Assert.Equal(0.95, (await restarted.GetAsync(f.Slug, f.Source.Id))!.JevConfidenceThreshold);
    }

    [Fact]
    public async Task Legacy_proposal_identity_and_history_survive_the_optional_context_fingerprint()
    {
        using var f = await Fixture.CreateAsync();
        var legacy = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))! with { ProcessorFingerprint = null };
        var payload = JsonSerializer.Serialize(legacy, JevDecisionStore.Json);
        Assert.DoesNotContain("processorFingerprint", payload);
        var originalId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
        await ReplaceProposalAsync(f, legacy);
        await f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, originalId, false, "Keep the original review");
        var reloaded = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        Assert.Equal(originalId, JevDecisionStore.ProposalId(reloaded));
        Assert.Equal(originalId, Assert.Single(await f.Store.ListAsync(f.Slug, f.Ticket.Id)).ProposalId);
    }

    [Fact]
    public async Task Concurrent_automatic_and_manual_acceptance_commit_only_one_transition_and_audit()
    {
        using var f = await Fixture.CreateAsync(); await ConfigureAutomaticAsync(f, true, 0.9);
        var proposal = (await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!;
        var id = JevDecisionStore.ProposalId(proposal);
        var signals = 0; f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        await Task.WhenAll(f.Decisions.TryApplyAutomaticallyAsync(f.Slug, f.Ticket.Id, id),
            f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, true));
        Assert.Equal(f.First.Id, (await f.Tickets.GetTicketAsync(f.Slug, f.Ticket.Id))!.ColumnId);
        Assert.Single(await f.Store.ListAsync(f.Slug, f.Ticket.Id)); Assert.Equal(1, signals);
    }

    private static async Task ConfigureAutomaticAsync(Fixture f, bool enabled, double threshold) =>
        f.Processor = await f.Processors.SaveAsync(f.Slug, f.Source.Id, "Qualifier", "Qualify work", null, true, 20, [], [], [],
            routes: f.Processor.Routes, defaultTargetColumnId: f.Processor.DefaultTargetColumnId,
            jevAutomaticRouting: enabled, jevConfidenceThreshold: threshold);

    private static async Task ReplaceProposalAsync(Fixture f, JevRoutingProposal proposal)
    {
        await using var db = f.Projects.GetProjectDb(f.Slug);
        var payload = JsonSerializer.Serialize(proposal, JevDecisionStore.Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JevObservations SET Payload = {payload} WHERE TicketId = {f.Ticket.Id}");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _temp = new();
        public ProjectService Projects = default!;
        public TicketService Tickets = default!;
        public ColumnProcessorService Processors = default!;
        public JevQualificationService Qualification = default!;
        public JevDecisionStore Store = default!;
        public JevProposalDecisionService Decisions = default!;
        public CaptureHandler Handler = new();
        private HttpClient _client = default!;
        public string Slug = "";
        public Ticket Ticket = default!;
        public List<BoardColumn> Columns = [];
        public BoardColumn Source = default!, First = default!, Second = default!;
        public ColumnProcessor Processor = default!;
        public static async Task<Fixture> CreateAsync(bool alternatives = true, bool postRun = false)
        {
            var f = new Fixture();
            f.Projects = new(f._temp.Path);
            f.Slug = (await f.Projects.CreateProjectAsync("Review decisions")).Slug;
            f.Columns = await new ColumnService(f.Projects).ListColumnsAsync(f.Slug);
            f.Source = f.Columns.Single(c => c.Name == "Backlog"); f.First = f.Columns.Single(c => c.Name == "Review"); f.Second = f.Columns.Single(c => c.Name == "Todo");
            f.Processors = new(f.Projects, new ProjectSkillService(f.Projects));
            f.Tickets = new(f.Projects, new MemberService(f.Projects), f.Processors);
            f.Processor = await f.Processors.SaveAsync(f.Slug, f.Source.Id, "Qualifier", "Qualify work", null, true, 20, [], [], [],
                routes: alternatives ? [new("review", f.First.Id)] : [], defaultTargetColumnId: alternatives ? f.Second.Id : f.First.Id,
                jevObservationOnly: !postRun, jevPostRunObservation: postRun);
            f.Ticket = await f.Tickets.CreateTicketAsync(f.Slug, "Do the work", columnId: f.Source.Id, pipelineId: f.Source.PipelineId);
            var vault = new ProjectSecretVault(f._temp.Path, new TestSecretProtector());
            await vault.SetAsync(f.Slug, JevQualificationService.SecretName, "test-key");
            f._client = new(f.Handler);
            f.Qualification = new(f.Projects, vault, f._client);
            f.Store = new(f.Projects); f.Decisions = new(f.Projects, f.Tickets, f.Processors, f.Store);
            if (!postRun) await f.Qualification.ObserveAsync(f.Slug, f.Processor, f.Ticket, f.Columns, CancellationToken.None);
            return f;
        }
        public void Dispose() { _client.Dispose(); _temp.Dispose(); }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Count;
        public JsonElement LastState;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            LastState = document.RootElement.GetProperty("state").Clone();
            var keys = document.RootElement.GetProperty("questions").GetProperty("routing").GetProperty("criteria").EnumerateObject().Select(p => p.Name).ToList();
            var selected = keys.First(k => k != "none");
            var answers = JsonSerializer.SerializeToNode(JevResponseFixture.Answers(selected))!;
            answers["routing"]!["probabilities"] = JsonSerializer.SerializeToNode(keys.ToDictionary(k => k, k => k == selected ? 1.0 : 0.0));
            answers["routing"]!["confidence"] = 1;
            Count++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { model = "jev-decision-fixture", answers }) };
        }
    }
}
