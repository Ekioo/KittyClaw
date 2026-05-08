using KittyClaw.ClaudeMock;

// Mock `claude` CLI: parses (and ignores) the flags KittyClaw sends, drains stdin,
// picks a scenario, replays its NDJSON on stdout. Exits with the scenario's code.
//
// Selection order:
//   1. KITTYCLAW_MOCK_SCENARIO env var (explicit override, used by tests)
//   2. Marker in stdin prompt: <!--scenario:NAME-->
//   3. Match by CLAUDE_AGENT env var → scenario file with same name
//   4. "default"

var sessionId = ArgParser.Get(args, "--session-id");
_ = ArgParser.Get(args, "--model");

var prompt = await Console.In.ReadToEndAsync();

var scenarioName =
    Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO")
    ?? ScenarioMatcher.FromPrompt(prompt)
    ?? Environment.GetEnvironmentVariable("CLAUDE_AGENT")
    ?? "default";

var loader = new ScenarioLoader(Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIOS_DIR"));
var scenario = loader.Load(scenarioName) ?? loader.Load("default");
if (scenario is null)
{
    await Console.Error.WriteLineAsync($"mock-claude: no scenario named '{scenarioName}' (and no default)");
    return 2;
}

return await ScenarioReplayer.ReplayAsync(scenario, sessionId);
