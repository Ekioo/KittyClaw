using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Automation;

public sealed class CostAccountingTests
{
    private static AgentRun Run() => new()
    {
        RunId = Guid.NewGuid().ToString("N"), ProjectSlug = "test", TicketId = null,
        AgentName = "test", SkillFile = "test", ConcurrencyGroup = "test", StartedAt = DateTime.UtcNow,
        Model = "gpt-6.1-sol"
    };

    private static void Usage(AgentRun run, int input, int cached, int output)
    {
        var json = JsonSerializer.Serialize(new { type = "turn.completed", usage = new { input_tokens = input,
            cached_input_tokens = cached, output_tokens = output } });
        using var doc = JsonDocument.Parse(json);
        CodexStreamAdapter.TryMap(doc.RootElement, json, run);
    }

    [Fact]
    public void Resume_SubtractsBaseline_AndIgnoresRepeatedCumulativeSummary()
    {
        var run = Run();
        run.CodexUsageBaseline = new(1000, 800, 100);
        Usage(run, 1600, 1300, 150);
        Usage(run, 1600, 1300, 150);
        Usage(run, 2000, 1600, 170);
        Assert.Equal(200, run.InputTokens);
        Assert.Equal(800, run.CacheReadTokens);
        Assert.Equal(70, run.OutputTokens);
        Assert.Equal(0.00118m, run.TotalCostUsd);
    }

    [Fact]
    public void MissingResumeBaseline_DoesNotChargeWholeSession()
    {
        var run = Run();
        run.CodexUsageBaseline = null;
        run.UsageIsReliable = false;
        Usage(run, 2_000_000, 1_900_000, 50000);
        Assert.Equal(0, run.TotalTokens);
        Assert.Null(run.TotalCostUsd);
        Assert.True(run.HasUsage);
    }

    [Fact]
    public void NewSessionAfterResume_CountsOnlyTheNewSession()
    {
        var run = Run();
        run.CodexUsageBaseline = new(1000, 800, 100);
        Usage(run, 1600, 1300, 150);
        run.CodexUsageBaseline = default(CodexTokenTotals);
        Usage(run, 100, 0, 10);
        Assert.Equal(200, run.InputTokens);
        Assert.Equal(500, run.CacheReadTokens);
        Assert.Equal(60, run.OutputTokens);
    }

    [Theory]
    [InlineData("codex:gpt-6-astra", 73.5)]
    [InlineData("codex:gpt-6.1-sol", 14.6)]
    [InlineData("codex:gpt-6-sol", 14.7)]
    [InlineData("codex:gpt-6-luna", 0.735)]
    public void MissingModels_ArePriced(string model, double expected)
    {
        Assert.True(ModelCostEstimator.TryEstimate(model, 1000000, 1000000, 1000000, 1000000, out var cost));
        Assert.Equal((decimal)expected, cost);
    }

    [Fact]
    public void LongContext_IsAppliedOnlyToIndividualRequests()
    {
        ModelCostEstimator.TryEstimate("gpt-6.1-sol", 300000, 10000, 0, 0, out var run);
        ModelCostEstimator.TryEstimate("gpt-6.1-sol", 300000, 10000, 0, 0, out var request, requestContext: true);
        Assert.Equal(0.7m, run);
        Assert.Equal(1.35m, request);
    }

    [Fact]
    public void HistoricalRepair_UsesOnlyRequestDeltasWithinRun_AndIsIdempotent()
    {
        using var temp = new TempDir();
        var session = Guid.NewGuid().ToString();
        var start = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(temp.Path, $"rollout-test-{session}.jsonl");
        string Point(DateTime at, int input, int cached, int output) => JsonSerializer.Serialize(new
        {
            timestamp = at, payload = new { type = "token_count", info = new
            { total_token_usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output } },
                rate_limits = new { plan_type = "pro" } }
        });
        File.WriteAllLines(path, [Point(start.AddSeconds(-1), 1000, 800, 100),
            Point(start.AddSeconds(1), 1600, 1300, 150), Point(start.AddSeconds(2), 1600, 1300, 150),
            Point(start.AddSeconds(3), 2000, 1600, 170), Point(start.AddSeconds(20), 3000, 2400, 200)]);
        var accounting = new CostHistoryAccounting(temp.Path, new CodexSessionUsage(temp.Path));
        var entry = new CostLogEntry(start.AddSeconds(10), "test", null, "gpt-6.1-sol", 400, 170, 1600, 0,
            0, 10, 0, SessionId: session, StartedAt: start);
        var corrected = accounting.Normalize(entry);
        Assert.Equal(200, corrected.InputTokens);
        Assert.Equal(800, corrected.CacheReadTokens);
        Assert.Equal(70, corrected.OutputTokens);
        Assert.Equal(0.00118m, corrected.UsdCost);
        Assert.Equal("pro", corrected.BillingPlan);
        Assert.True(corrected.CostKnown);
        Assert.Equal(corrected, accounting.Normalize(corrected));
        Assert.Equal(0, entry.UsdCost);
    }

    [Fact]
    public void MissingHistoricalEvidence_IsExplicitlyUnpriced()
    {
        using var temp = new TempDir();
        var entry = new CostLogEntry(DateTime.UtcNow, "test", null, "gpt-6.1-sol", 400, 170, 1600, 0,
            12, 10, 0, CostEstimated: true, RunId: Guid.NewGuid().ToString("N"));
        var corrected = new CostHistoryAccounting(temp.Path).Normalize(entry);
        Assert.False(corrected.CostKnown);
        Assert.Equal(0, corrected.UsdCost);
    }

    [Fact]
    public async Task CorrectedJournalRevision_ReplacesOriginal_InReportAndBudget_AcrossRotation()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("Accounting revisions");
        var tickets = new TicketService(projects, new MemberService(projects));
        var workspace = projects.ResolveWorkspacePath(project);
        var tracker = new CostTracker();
        var now = DateTime.UtcNow;
        var entry = new CostLogEntry(now, "test", null, "gpt-6.1-sol", 400, 170, 1600, 0,
            12, 10, 0, RunId: "revision", CostEstimated: true);
        tracker.LogRun(workspace, entry);
        var channel = Path.Combine(workspace, ".agents", "channel");
        File.Move(Path.Combine(channel, "cost-log.jsonl"), Path.Combine(channel, "cost-log-2026-10.jsonl"));
        tracker.LogRun(workspace, entry with { UsdCost = 0.00118m, InputTokens = 200,
            CacheReadTokens = 800, OutputTokens = 70, UsageVersion = 2,
            BillingPlan = "pro", RateCardVersion = ModelCostEstimator.RateCardVersion });
        var reports = new CostReportService(projects, new PipelineService(projects), tickets, null, tracker);
        await reports.RefreshAsync();
        await reports.RefreshAsync();
        var day = DateOnly.FromDateTime(now.ToLocalTime());
        var result = await reports.GetReportAsync(new(day, day));
        Assert.Equal(0.00118m, result.TotalUsd);
        Assert.Equal(0, result.UnpricedRuns);
        Assert.Equal("pro", Assert.Single(result.Subscriptions!).Plan);
        Assert.False(tracker.IsBudgetExceeded(workspace, 1m));
        Assert.True(tracker.IsBudgetExceeded(workspace, 0.001m));
        Assert.Equal(2, Directory.GetFiles(channel, "cost-log*.jsonl").Sum(path => File.ReadAllLines(path).Length));
        var restarted = new CostReportService(projects, new PipelineService(projects), tickets, null, tracker);
        Assert.Equal(result.TotalUsd, (await restarted.GetReportAsync(new(day, day))).TotalUsd);
        Assert.Empty((await restarted.GetReportAsync(new(day, day, Model: "jev-1.14.0"))).Subscriptions!);
    }

    [Fact]
    public void ArchivedRollouts_AreAvailableForHistoricalRepair()
    {
        using var temp = new TempDir();
        var session = Guid.NewGuid().ToString();
        var archive = Path.Combine(temp.Path, "archived_sessions");
        Directory.CreateDirectory(archive);
        var at = DateTime.UtcNow;
        File.WriteAllText(Path.Combine(archive, $"rollout-test-{session}.jsonl"), JsonSerializer.Serialize(new
        {
            timestamp = at, payload = new { type = "token_count", info = new
            { total_token_usage = new { input_tokens = 100, cached_input_tokens = 80, output_tokens = 5 } },
                rate_limits = new { plan_type = "pro" } }
        }));
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", temp.Path);
        try
        {
            var point = Assert.Single(new CodexSessionUsage().Read(session));
            Assert.Equal(100, point.Total.Input);
            Assert.Equal("pro", point.Plan);
            var entry = new CostLogEntry(at.AddSeconds(1), "test", null, "gpt-6.1-sol", 20, 5, 80, 0,
                0, 2, 0, SessionId: session, StartedAt: at.AddSeconds(-1));
            var repaired = new CostHistoryAccounting(temp.Path).Normalize(entry);
            Assert.True(repaired.CostKnown);
            Assert.Equal(0.000098m, repaired.UsdCost);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
    }
}
