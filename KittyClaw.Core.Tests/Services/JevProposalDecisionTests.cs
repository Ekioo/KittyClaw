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

    [Fact]
    public async Task Audit_failure_rolls_back_transition_and_emits_no_status_signal()
    {
        using var f = await Fixture.CreateAsync();
        var id = JevDecisionStore.ProposalId((await f.Qualification.GetAsync(f.Slug, f.Ticket.Id))!);
        await f.Store.ListAsync(f.Slug, f.Ticket.Id);
        await using (var db = f.Projects.GetProjectDb(f.Slug))
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_jev_review BEFORE INSERT ON JevProposalDecisions BEGIN SELECT RAISE(ABORT, 'audit unavailable'); END");
        var signals = 0; f.Tickets.TicketStatusChanged += (_, _, _, _) => signals++;
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => f.Decisions.DecideAsync(f.Slug, f.Ticket.Id, id, true));
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
