using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class ProjectSecretInjectionTests
{
    [Fact]
    public async Task Escaped_json_secret_is_removed_before_events_are_published_and_saved()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("JSON log fixture");
        var workspace = projects.ResolveWorkspacePath(project);
        const string secret = "fixture-\u00e9-value";
        const string encoded = """fixture-\u00e9-value""";
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(project.Slug, "PROJECT_TOKEN", secret);
        var scenarios = Directory.CreateDirectory(Path.Combine(temp.Path, "scenarios")).FullName;
        await File.WriteAllLinesAsync(Path.Combine(scenarios, "escaped-secret.ndjson"),
        [
            """{"type":"system","subtype":"init","session_id":"{{session_id}}"}""",
            $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"Observed {{{encoded}}}"}]}}""",
            $$$"""{"type":"result","subtype":"success","is_error":false,"result":"Observed {{{encoded}}}","usage":{"input_tokens":3}}""",
        ]);
        TestSkillBuilder.Create(workspace, "secret-agent", scenario: "escaped-secret");
        var logs = new RunLogStore(temp.Path);
        var registry = new AgentRunRegistry(logs);
        var runner = new AgentRunner(new SessionRegistry(), registry, new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance, projectSecrets: vault);
        var published = new System.Collections.Concurrent.ConcurrentQueue<StreamEvent>();

        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "secret-agent",
            SkillFile = "secret-agent/SKILL.md",
            MaxTurns = 1,
            OnEventHook = published.Enqueue,
            Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarios },
        }, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(3, run.InputTokens);
        foreach (var events in new IEnumerable<StreamEvent>[] { published, Assert.Single(logs.LoadAll()).SnapshotBuffer() })
        {
            Assert.Contains(events, item => item.Kind == "assistant" && item.Text.Contains("Observed [REDACTED]"));
            Assert.Contains(events, item => item.Kind == "result" && item.Detail?.Contains("Observed [REDACTED]") == true);
            Assert.DoesNotContain(events, item =>
                item.Text.Contains(secret) || item.Text.Contains(encoded) ||
                item.Detail?.Contains(secret) == true || item.Detail?.Contains(encoded) == true);
        }
    }

    [Fact]
    public async Task Agent_receives_only_its_project_secrets_and_stream_is_redacted()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var alpha = await projects.CreateProjectAsync("Alpha secrets");
        var beta = await projects.CreateProjectAsync("Beta secrets");
        var alphaWorkspace = projects.ResolveWorkspacePath(alpha);
        var betaWorkspace = projects.ResolveWorkspacePath(beta);
        const string secret = "agent-secret-value-278";
        var vault = new ProjectSecretVault(temp.Path, new TestSecretProtector());
        await vault.SetAsync(alpha.Slug, "PROJECT_TOKEN", secret);

        var scenarioDirectory = Path.Combine(temp.Path, "scenarios");
        Directory.CreateDirectory(scenarioDirectory);
        await File.WriteAllTextAsync(Path.Combine(scenarioDirectory, "project-secret.ndjson"), string.Join('\n',
        [
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"{{session_id}}\",\"model\":\"mock\"}",
            "{\"_meta\":{\"write_env\":{\"path\":\"observed-secret.txt\",\"name\":\"PROJECT_TOKEN\"}}}",
            "{\"_meta\":{\"emit_env\":\"PROJECT_TOKEN\"}}",
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":1,\"num_turns\":1}"
        ]));
        TestSkillBuilder.Create(alphaWorkspace, "secret-agent", scenario: "project-secret");
        TestSkillBuilder.Create(betaWorkspace, "secret-agent", scenario: "project-secret");
        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance, projectSecrets: vault);

        async Task<AgentRun> Run(string slug, string workspace) => await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = slug,
            WorkspacePath = workspace,
            AgentName = "secret-agent",
            SkillFile = "secret-agent/SKILL.md",
            MaxTurns = 1,
            Env = new Dictionary<string, string> { ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = scenarioDirectory },
        }, CancellationToken.None);

        var alphaRun = await Run(alpha.Slug, alphaWorkspace);
        var betaRun = await Run(beta.Slug, betaWorkspace);

        Assert.Equal(AgentRunStatus.Completed, alphaRun.Status);
        Assert.Equal(secret, await File.ReadAllTextAsync(Path.Combine(alphaWorkspace, "observed-secret.txt")));
        Assert.DoesNotContain(alphaRun.SnapshotBuffer(), item =>
            item.Text.Contains(secret, StringComparison.Ordinal) || (item.Detail?.Contains(secret, StringComparison.Ordinal) ?? false));
        Assert.Contains(alphaRun.SnapshotBuffer(), item => item.Text.Contains(SecretRedactor.Replacement));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(Path.Combine(betaWorkspace, "observed-secret.txt")));
    }
}
