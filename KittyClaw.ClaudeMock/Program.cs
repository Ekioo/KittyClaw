using KittyClaw.ClaudeMock;

// Mock `claude` CLI: parses (and ignores) the flags KittyClaw sends, drains stdin,
// picks a scenario, replays its NDJSON on stdout. Exits with the scenario's code.
//
// Selection order:
//   1. KITTYCLAW_MOCK_SCENARIO env var (explicit override, used by tests)
//   2. Marker in stdin prompt: <!--scenario:NAME-->
//   3. Match by CLAUDE_AGENT env var → scenario file with same name
//   4. "default"

var sessionId = ArgParser.Get(args, "--session-id") ?? ArgParser.Get(args, "-s");
var model = ArgParser.Get(args, "--model");

if (args.Contains("--version"))
{
    var readinessFile = Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_READINESS_FILE");
    if (!string.IsNullOrWhiteSpace(readinessFile) && !File.Exists(readinessFile)) return 1;
    Console.WriteLine("kittyclaw-mock 1.0");
    return 0;
}

// Claude: prompt arrives on stdin. Grok (KittyClaw headless): --prompt-file, empty stdin.
// Read the file when present so scenario markers in the prompt still resolve.
var promptFile = ArgParser.Get(args, "--prompt-file");
string prompt;
if (!string.IsNullOrEmpty(promptFile) && File.Exists(promptFile))
    prompt = await File.ReadAllTextAsync(promptFile);
else
    prompt = await Console.In.ReadToEndAsync();

var unavailableModel = Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_UNAVAILABLE_MODEL");
var scenarioName =
    (!string.IsNullOrWhiteSpace(unavailableModel)
     && string.Equals(model, unavailableModel, StringComparison.OrdinalIgnoreCase)
        ? "model-unavailable"
        : null)
    ?? Environment.GetEnvironmentVariable("KITTYCLAW_MOCK_SCENARIO")
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

if (args.FirstOrDefault() == "exec")
{
    Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"mock-codex-thread\"}");
    Console.WriteLine("{\"type\":\"turn.started\"}");
    Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"{\\\"outcome\\\":\\\"done\\\",\\\"skillsUsed\\\":[],\\\"summary\\\":\\\"First run complete.\\\"}\"}}");
    Console.WriteLine("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":10}}");
    return 0;
}
if (args.Contains("--output-format") && args.Contains("streaming-json"))
{
    Console.WriteLine("{\"type\":\"text\",\"data\":\"{\\\"outcome\\\":\\\"done\\\",\\\"skillsUsed\\\":[],\\\"summary\\\":\\\"First run complete.\\\"}\"}");
    Console.WriteLine("{\"type\":\"end\",\"stopReason\":\"EndTurn\",\"usage\":{\"input_tokens\":10,\"output_tokens\":10}}");
    return 0;
}

// Real claude loads PreToolUse/PostToolUse hooks from --settings; the mock honors the same file so
// hermetic tests can prove that a denied hook verdict prevents the protected effect.
var hooks = HookSettings.Load(ArgParser.Get(args, "--settings"));

return await ScenarioReplayer.ReplayAsync(scenario, sessionId, Directory.GetCurrentDirectory(), hooks);
