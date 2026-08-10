using System.Text.Json;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class ColumnAgentDispatcherFirstRunIntegrationTests
{
    [Fact]
    public async Task First_qualify_run_completes_with_selected_primary_and_correlated_events()
    {
        using var fixture = new FirstRunFixture(claude: true, grok: false);
        var plan = await fixture.FirstRun.SelectAsync("journey-primary");
        await fixture.FirstRun.MarkStartedAsync("journey-primary");

        var result = await fixture.DispatchAsync(plan, "journey-primary");
        var events = await fixture.ReadEventsAsync();

        Assert.Equal(AgentRunStatus.Completed, result.Run.Status);
        Assert.NotNull(result.Result);
        Assert.Null(result.Error);
        Assert.Equal("done", result.Result!.Outcome);
        Assert.Equal(
            ["provider_selected", "first_run_started", "first_run_completed"],
            events.Select(e => e.Name).ToArray());
        Assert.All(events, e => Assert.Equal("journey-primary", e.JourneyId));
        Assert.Equal("claude", events[0].Provider);
        Assert.NotNull(events[^1].DurationMilliseconds);
    }

    [Fact]
    public async Task First_qualify_run_records_primary_failure_then_completes_with_available_fallback()
    {
        using var fixture = new FirstRunFixture(claude: true, grok: true);
        var plan = await fixture.FirstRun.SelectAsync("journey-fallback");
        await fixture.FirstRun.MarkStartedAsync("journey-fallback");
        Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_UNAVAILABLE_MODEL", "claude-sonnet-4-6");

        var result = await fixture.DispatchAsync(plan, "journey-fallback");
        var events = await fixture.ReadEventsAsync();

        Assert.Equal(AgentRunStatus.Completed, result.Run.Status);
        Assert.Contains(result.Run.SnapshotBuffer(), e => e.Kind == "fallback");
        Assert.Equal("grok-4.5", result.Run.Model);
        Assert.Equal(
            ["provider_selected", "first_run_started", "provider_failed", "provider_fallback_started", "first_run_completed"],
            events.Select(e => e.Name).ToArray());
        Assert.All(events, e => Assert.Equal("journey-fallback", e.JourneyId));
        Assert.Equal("claude", events[2].Provider);
        Assert.Equal("grok", events[2].FallbackProvider);
        Assert.Equal("grok", events[3].Provider);
        Assert.NotNull(events[^1].DurationMilliseconds);
    }

    private sealed class FirstRunFixture : IDisposable
    {
        private readonly TempDir _temp = new();
        private readonly string? _previousGrok;
        private readonly string? _previousUnavailableModel;
        private readonly string? _previousScenarios;
        private readonly ProjectService _projects;
        private readonly ColumnAgentDispatcher _dispatcher;
        private readonly string _scenarioDirectory;
        private readonly string _eventFile;

        public FirstRunProviderService FirstRun { get; }

        public FirstRunFixture(bool claude, bool grok)
        {
            _previousGrok = Environment.GetEnvironmentVariable("KITTYCLAW_GROK_BIN");
            _previousUnavailableModel = Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_UNAVAILABLE_MODEL");
            _previousScenarios = Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIOS_DIR");

            var mock = Environment.GetEnvironmentVariable("KITTYCLAW_CLAUDE_BIN");
            Assert.False(string.IsNullOrWhiteSpace(mock), "MockClaude fixture did not resolve the mock binary.");
            Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", grok ? mock : null);
            GrokCli.ResetForTests();

            _scenarioDirectory = Path.Combine(_temp.Path, "scenarios");
            Directory.CreateDirectory(_scenarioDirectory);
            File.WriteAllText(Path.Combine(_scenarioDirectory, "default.ndjson"), """
                {"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}
                {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"{\"outcome\":\"done\",\"skillsUsed\":[],\"summary\":\"First run complete.\"}"}]}}
                {"type":"result","subtype":"success","is_error":false,"duration_ms":42,"num_turns":1}
                {"_meta":{"exit":0}}
                """);
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIOS_DIR", _scenarioDirectory);
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_UNAVAILABLE_MODEL", null);

            _projects = new ProjectService(_temp.Path);
            var readiness = new AgentCliReadinessService(
                () => "claude", () => null, () => grok ? "grok" : null,
                (_, _, _) => Task.FromResult(claude || grok));
            FirstRun = new FirstRunProviderService(_temp.Path, readiness);
            _eventFile = Path.Combine(_temp.Path, "activation", "first-run-events.jsonl");

            var skills = new ProjectSkillService(_projects);
            var processors = new ColumnProcessorService(_projects, skills);
            var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
                NullLogger<AgentRunner>.Instance);
            _dispatcher = new ColumnAgentDispatcher(runner, _projects, skills, processors, FirstRun);
        }

        public async Task<ColumnDispatchResult> DispatchAsync(FirstRunProviderPlan plan, string journeyId)
        {
            var project = await _projects.CreateProjectAsync($"first-run-{Guid.NewGuid():N}");
            var workspace = _projects.ResolveWorkspacePath(project);
            Directory.CreateDirectory(workspace);
            await _projects.UpdateProjectAsync(project.Slug, workspace, plan.Fallback?.Model, updateFallback: true);

            var processor = new ColumnProcessor
            {
                Id = 1,
                ColumnId = 1,
                Name = "Qualify",
                Mission = "Qualify the first ticket.",
                Model = plan.Primary?.Model ?? "claude-sonnet-4-6",
                MaxTurns = 1,
            };
            var execution = new ColumnExecution { Id = Guid.NewGuid().ToString("N"), ProcessorId = 1, TicketId = 1 };
            var ticket = new Ticket
            {
                Id = 1,
                Title = "First task",
                Status = "Qualify",
                Description = $"Journey: `{journeyId}`",
            };

            return await _dispatcher.DispatchAsync(project.Slug, processor, execution, ticket, CancellationToken.None);
        }

        public async Task<List<FirstRunActivationEvent>> ReadEventsAsync() =>
            (await File.ReadAllLinesAsync(_eventFile))
                .Select(line => JsonSerializer.Deserialize<FirstRunActivationEvent>(line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToList();

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_GROK_BIN", _previousGrok);
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_UNAVAILABLE_MODEL", _previousUnavailableModel);
            Environment.SetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIOS_DIR", _previousScenarios);
            GrokCli.ResetForTests();
            _temp.Dispose();
        }
    }
}
