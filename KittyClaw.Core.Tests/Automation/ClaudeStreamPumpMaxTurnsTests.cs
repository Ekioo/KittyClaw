using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Integration tests for the max-turns detection in ClaudeStreamPump.
///
/// Test cases 3–6 from the architect plan (Continue clicked, user types, drawer reopen,
/// stop-then-reopen) exercise ClaudeChatDrawer — a Blazor component in KittyClaw.Web.
/// Testing those from Core.Tests requires bunit; they are out of scope here.
/// </summary>
[Collection("MockClaude")]
public class ClaudeStreamPumpMaxTurnsTests : IDisposable
{
    // Write the max-turns scenario to a temp dir so the ClaudeMock binary can find it
    // via KITTYCLAW_MOCK_SCENARIOS_DIR — avoids committing non-test files.
    private readonly string _scenariosDir = Path.Combine(Path.GetTempPath(), $"kc-scenarios-{Guid.NewGuid():N}");

    public ClaudeStreamPumpMaxTurnsTests()
    {
        Directory.CreateDirectory(_scenariosDir);
        File.WriteAllText(
            Path.Combine(_scenariosDir, "max-turns.ndjson"),
            """
            {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I started working but hit the turn limit."}]}}
            {"type":"result","subtype":"error_max_turns","is_error":true,"duration_ms":42,"num_turns":5}
            {"_meta":{"exit":1}}
            """);
    }

    public void Dispose() => Directory.Delete(_scenariosDir, recursive: true);

    // ── Case 1: Normal completion ────────────────────────────────────────────
    // Claude exits normally — no max-turns event must appear in the run buffer.
    [Fact]
    public async Task NormalCompletion_NoMaxTurnsEvent()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("pump-normal");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "default");

        var run = await RunWithScenario(workspace, project.Slug, "default");

        Assert.DoesNotContain(run.SnapshotBuffer(), e => e.Kind == "max_turns");
    }

    // ── Case 2: Max-turns hit → pump emits max_turns kind ───────────────────
    // Claude emits {"type":"result","subtype":"error_max_turns"} → pump must push
    // Kind == "max_turns", not "result".
    [Fact]
    public async Task MaxTurnsHit_PumpEmitsMaxTurnsKind()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("pump-maxturn");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "max-turns");

        var run = await RunWithScenario(workspace, project.Slug, "max-turns");

        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "max_turns");
    }

    // ── Case 2b: max_turns event must not be mislabelled as "result" ─────────
    // The UI handler must be able to distinguish "result" (normal end) from
    // "max_turns" (limit hit) — they must never coexist for the terminal event.
    [Fact]
    public async Task MaxTurnsHit_TerminalEventIsNotLabelledResult()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("pump-maxturn2");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        TestSkillBuilder.Create(workspace, "test-agent", scenario: "max-turns");

        var run = await RunWithScenario(workspace, project.Slug, "max-turns");

        Assert.DoesNotContain(run.SnapshotBuffer(), e => e.Kind == "result");
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private async Task<AgentRun> RunWithScenario(string workspace, string slug, string scenario)
    {
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var gate = new RunConcurrencyGate(maxConcurrent: 1);
        var runner = new ClaudeRunner(sessions, runs, gate, NullLogger<ClaudeRunner>.Instance);

        var ctx = new ClaudeRunContext
        {
            ProjectSlug = slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "test-agent/SKILL.md",
            MaxTurns = 5,
            Env = new Dictionary<string, string>
            {
                ["KITTYCLAW_MOCK_SCENARIO"] = scenario,
                ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = _scenariosDir,
            },
        };

        return await runner.RunAsync(ctx, CancellationToken.None);
    }
}
