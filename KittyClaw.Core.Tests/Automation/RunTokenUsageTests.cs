using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Token-cost capture chain: result-event usage → AgentRun accumulation → snapshot
/// persistence → cost-log + per-ticket totals (RunCostRecorder).
/// </summary>
[Collection("MockClaude")]
public class RunTokenUsageTests : IDisposable
{
    private readonly string _scenariosDir = Path.Combine(Path.GetTempPath(), $"kc-scenarios-{Guid.NewGuid():N}");

    public RunTokenUsageTests()
    {
        Directory.CreateDirectory(_scenariosDir);
        File.WriteAllText(
            Path.Combine(_scenariosDir, "usage.ndjson"),
            """
            {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}
            {"type":"result","subtype":"success","is_error":false,"duration_ms":42,"total_cost_usd":0.1234,"usage":{"input_tokens":10,"output_tokens":200,"cache_read_input_tokens":3000,"cache_creation_input_tokens":400}}
            {"_meta":{"exit":0}}
            """);
    }

    public void Dispose() => Directory.Delete(_scenariosDir, recursive: true);

    // ── Stream pump: usage in the result event lands on the AgentRun ─────────
    [Fact]
    public async Task ResultEventUsage_IsAccumulatedOnRun()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("usage-pump");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        TestSkillBuilder.Create(workspace, "test-agent", scenario: "usage");

        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(),
            new RunConcurrencyGate(1), NullLogger<AgentRunner>.Instance);
        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "test-agent/SKILL.md",
            MaxTurns = 5,
            Env = new Dictionary<string, string>
            {
                ["KITTYCLAW_MOCK_SCENARIO"] = "usage",
                ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = _scenariosDir,
            },
        }, CancellationToken.None);

        Assert.Equal(10, run.InputTokens);
        Assert.Equal(200, run.OutputTokens);
        Assert.Equal(3000, run.CacheReadTokens);
        Assert.Equal(400, run.CacheWriteTokens);
        Assert.Equal(3610, run.TotalTokens);
        Assert.Equal(0.1234m, run.TotalCostUsd);
    }

    // ── AddUsage sums across attempts (fallback retry, steer replay) ─────────
    [Fact]
    public void AddUsage_AccumulatesAcrossAttempts()
    {
        var run = NewRun("r1", ticketId: null);
        run.AddUsage(10, 20, 30, 40, 0.5m);
        run.AddUsage(1, 2, 3, 4, 0.25m);

        Assert.Equal(11, run.InputTokens);
        Assert.Equal(22, run.OutputTokens);
        Assert.Equal(33, run.CacheReadTokens);
        Assert.Equal(44, run.CacheWriteTokens);
        Assert.Equal(0.75m, run.TotalCostUsd);
    }

    // ── Snapshot persistence round-trips usage ────────────────────────────────
    [Fact]
    public void RunLogStore_RoundTripsUsage()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);

        var run = NewRun("usage-run", ticketId: 7);
        run.AddUsage(10, 200, 3000, 400, 0.1234m);
        run.Status = AgentRunStatus.Completed;
        run.EndedAt = DateTime.UtcNow;
        store.Save(run);

        var loaded = store.LoadAll().Single(r => r.RunId == "usage-run");
        Assert.Equal(10, loaded.InputTokens);
        Assert.Equal(200, loaded.OutputTokens);
        Assert.Equal(3000, loaded.CacheReadTokens);
        Assert.Equal(400, loaded.CacheWriteTokens);
        Assert.Equal(0.1234m, loaded.TotalCostUsd);
        Assert.False(loaded.CostIsEstimated);
    }

    [Fact]
    public void RunLogStore_RoundTripsEstimatedCostMarker()
    {
        using var tmp = new TempDir();
        var store = new RunLogStore(tmp.Path);
        var run = NewRun("estimated-run", ticketId: null);
        run.AddUsage(100, 20, 0, 0, 0.0011m, costIsEstimated: true);
        run.Status = AgentRunStatus.Completed;
        run.EndedAt = DateTime.UtcNow;
        store.Save(run);

        var loaded = store.LoadAll().Single(r => r.RunId == "estimated-run");
        Assert.True(loaded.CostIsEstimated);
        Assert.Equal(0.0011m, loaded.TotalCostUsd);
    }

    // ── RunCostRecorder: cost-log line + durable ticket totals ────────────────
    [Fact]
    public async Task RecordAsync_WritesCostLogAndTicketTotals()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("usage-recorder");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Costly work");

        var run = NewRun("rec-run", ticket.Id, project.Slug);
        run.AddUsage(10, 200, 3000, 400, 0.1234m);
        run.Status = AgentRunStatus.Completed;
        run.EndedAt = run.StartedAt.AddSeconds(30);
        run.ExitCode = 0;

        var recorder = new RunCostRecorder(new AgentRunRegistry(), new CostTracker(),
            projects, tickets, NullLogger<RunCostRecorder>.Instance);
        await recorder.RecordAsync(run);

        var logPath = Path.Combine(workspace, ".agents", "channel", "cost-log.jsonl");
        Assert.True(File.Exists(logPath));
        var line = File.ReadAllLines(logPath).Single();
        Assert.Contains("\"UsdCost\":0.1234", line);
        Assert.Contains($"\"TicketId\":{ticket.Id}", line);
        Assert.Contains($"\"ProjectSlug\":\"{project.Slug}\"", line);
        Assert.Contains($"\"PipelineId\":{ticket.PipelineId}", line);
        Assert.Contains("\"RunId\":\"rec-run\"", line);

        var updated = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.NotNull(updated);
        Assert.Equal(3610, updated!.AgentTokens);
        Assert.Equal(0.1234, updated.AgentCostUsd, precision: 6);
        Assert.False(updated.AgentCostEstimated);

        // Second run accumulates on top of the first.
        await recorder.RecordAsync(run);
        updated = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.Equal(7220, updated!.AgentTokens);
    }

    [Fact]
    public async Task RecordAsync_PersistsEstimatedCostMarkerOnTicket()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("estimated-recorder");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Estimated work");
        var run = NewRun("estimated-rec", ticket.Id, project.Slug);
        run.AddUsage(100, 20, 0, 0, 0.0011m, costIsEstimated: true);
        run.Status = AgentRunStatus.Completed;
        run.EndedAt = DateTime.UtcNow;

        var recorder = new RunCostRecorder(new AgentRunRegistry(), new CostTracker(),
            projects, tickets, NullLogger<RunCostRecorder>.Instance);
        await recorder.RecordAsync(run);

        var updated = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.NotNull(updated);
        Assert.True(updated!.AgentCostEstimated);
        Assert.Equal(0.0011, updated.AgentCostUsd, precision: 6);
    }

    private static AgentRun NewRun(string runId, int? ticketId, string slug = "p") => new()
    {
        RunId = runId, ProjectSlug = slug, TicketId = ticketId,
        AgentName = "test-agent", SkillFile = "test-agent/SKILL.md",
        ConcurrencyGroup = "test-agent", StartedAt = DateTime.UtcNow,
    };
}
