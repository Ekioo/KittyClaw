using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public sealed class JevCostRecorderTests
{
    [Fact]
    public async Task Costs_include_all_calls_tokens_filters_unknown_prices_and_survive_snapshot_reload()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("JEV costs");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Review work");
        var tracker = new CostTracker();
        var reports = new CostReportService(projects, new PipelineService(projects), tickets, null, tracker);
        var recorder = new JevCostRecorder(projects, tracker, reports, NullLogger<JevCostRecorder>.Instance);
        var known = JsonSerializer.SerializeToElement(new { model = "jev-1.14.0", usage = new { input_tokens = 1000, output_tokens = 50 } });
        var unknown = JsonSerializer.SerializeToElement(new { model = "jev-future", usage = new { input_tokens = 500, output_tokens = 20 } });
        await recorder.RecordAsync(project.Slug, ticket, "profile", known, 0.1);
        await recorder.RecordAsync(project.Slug, ticket, "acceptance", known, 0.1);
        await recorder.RecordAsync(project.Slug, ticket, "qualification", unknown, 0.1);
        tracker.LogRun(projects.ResolveWorkspacePath(project), new(DateTime.UtcNow, "Agent", ticket.Id, "other", 10, 20, 0, 0, 1m, 1, 0, ProjectSlug: project.Slug, PipelineId: ticket.PipelineId, RunId: "agent"));
        await reports.RefreshAsync();
        var day = DateOnly.FromDateTime(DateTime.Now);
        var report = await reports.GetReportAsync(new(day, day));
        Assert.Equal(1.000084m, report.TotalUsd);
        var total = Assert.Single(report.Projects);
        Assert.Equal(new JevCostSummary(3, 2500, 120, .000084m, 1), total.Jev);
        Assert.True(report.Estimated);
        var filtered = await reports.GetReportAsync(new(day, day, Model: "jev-1.14.0", PipelineKey: $"{project.Slug}:{ticket.PipelineId}"));
        Assert.Equal(.000084m, filtered.TotalUsd);
        Assert.Equal(2, Assert.Single(filtered.Projects).Jev!.Calls);
        Assert.Empty((await reports.GetReportAsync(new(day.AddDays(-2), day.AddDays(-1)))).Projects);
        Assert.Empty((await reports.GetReportAsync(new(day, day, new HashSet<string> { "another-project" }))).Projects);
        var restarted = new CostReportService(projects, new PipelineService(projects), tickets, null, tracker);
        Assert.Equal(total.Jev, Assert.Single((await restarted.GetReportAsync(new(day, day))).Projects).Jev);
        await restarted.RefreshAsync();
        Assert.Equal(3, Assert.Single((await restarted.GetReportAsync(new(day, day))).Projects).Jev!.Calls);
        Assert.Contains((await reports.GetOptionsAsync()).Models, m => m.Key == "jev-1.14.0");
        Assert.False(ModelCostEstimator.TryEstimate("jev-future", 1000, 0, 0, 0, out _));
        Assert.True(ModelCostEstimator.TryEstimate("jev-1.14.0", 1_000_000, 999999, 0, 0, out var rate));
        Assert.Equal(.042m, rate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualification_accounts_for_a_response_even_if_its_judgment_is_invalid_and_deduplicates_observations(bool invalid)
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Qualification costs");
        var board = await new ColumnService(projects).ListColumnsAsync(project.Slug);
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        var processor = await processors.SaveAsync(project.Slug, board[0].Id, "Qualify", "Qualify work", null,
            true, 20, [], [], [], defaultTargetColumnId: board[1].Id, jevObservationOnly: true);
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Check request", status: board[0].Name, pipelineId: board[0].PipelineId, columnId: board[0].Id);
        var reports = new CostReportService(projects, new PipelineService(projects), tickets);
        var tracker = new CostTracker();
        var recorder = new JevCostRecorder(projects, tracker, reports, NullLogger<JevCostRecorder>.Instance);
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(project.Slug, JevQualificationService.SecretName, "fake-test-key");
        using var client = new HttpClient(new Handler(board[1].Id, invalid));
        var qualification = new JevQualificationService(projects, vault, client, costRecorder: recorder);
        await qualification.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        await qualification.ObserveAsync(project.Slug, processor, ticket, board, CancellationToken.None);
        Assert.Equal(invalid, (await qualification.GetAsync(project.Slug, ticket.Id))!.Error is not null);
        await reports.RefreshAsync();
        var day = DateOnly.FromDateTime(DateTime.Now);
        var total = Assert.Single((await reports.GetReportAsync(new(day, day))).Projects);
        Assert.Equal(new JevCostSummary(1, 1000, 50, .000042m), total.Jev);
    }

    [Fact]
    public async Task Legacy_usage_is_imported_once_and_remains_after_observations_are_replaced()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Legacy JEV usage");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Historical qualification");
        var proposal = new JevRoutingProposal("old", ticket.ColumnId!.Value, DateTime.UtcNow,
            "jev-1.14.0", "none", 1, new() { ["none"] = 1 }, new() { ["none"] = "No route" }, null,
            Usage: new(1000, 30));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            await JevDecisionStore.PrepareAsync(db);
            var payload = JsonSerializer.Serialize(proposal, options);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JevObservations (TicketId, Payload) VALUES ({ticket.Id}, {payload})");
            // The same API response can also occur in the immutable decision audit.
            var decision = new JevProposalDecision(JevDecisionStore.ProposalId(proposal), ticket.Id, false, DateTime.UtcNow, "owner", "Review later", proposal, null);
            var audit = JsonSerializer.Serialize(decision, options);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JevProposalDecisions (ProposalId, TicketId, Payload) VALUES ({decision.ProposalId}, {ticket.Id}, {audit})");
        }
        var reports = new CostReportService(projects, new PipelineService(projects), tickets);
        var day = DateOnly.FromDateTime(DateTime.Now);
        await reports.RefreshAsync(); await reports.RefreshAsync();
        Assert.Equal(new JevCostSummary(1, 1000, 30, .000042m), Assert.Single((await reports.GetReportAsync(new(day, day))).Projects).Jev);
        await using (var db = projects.GetProjectDb(project.Slug))
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM JevObservations; DELETE FROM JevProposalDecisions");
        }
        var restarted = new CostReportService(projects, new PipelineService(projects), tickets);
        await restarted.RefreshAsync();
        Assert.Equal(new JevCostSummary(1, 1000, 30, .000042m), Assert.Single((await restarted.GetReportAsync(new(day, day))).Projects).Jev);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"input_tokens\":\"invalid\",\"output_tokens\":10}")]
    public async Task Missing_or_invalid_usage_is_visible_as_unpriced_instead_of_free(string usage)
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Missing usage");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Review");
        var reports = new CostReportService(projects, new PipelineService(projects), tickets);
        var recorder = new JevCostRecorder(projects, new CostTracker(), reports, NullLogger<JevCostRecorder>.Instance);
        using var body = JsonDocument.Parse("{\"model\":\"jev-1.14.0\",\"usage\":" + usage + "}");
        await recorder.RecordAsync(project.Slug, ticket, "review", body.RootElement, .1);
        await reports.RefreshAsync(); var day = DateOnly.FromDateTime(DateTime.Now);
        Assert.Equal(1, Assert.Single((await reports.GetReportAsync(new(day, day))).Projects).Jev!.UnpricedCalls);
    }

    private sealed class Handler(int target, bool invalid) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                model = "jev-1.14.0", usage = new { input_tokens = 1000, output_tokens = 50 },
                answers = JevResponseFixture.Answers($"column-{target}", invalid ? 2 : .15)
            }) });
    }
}
