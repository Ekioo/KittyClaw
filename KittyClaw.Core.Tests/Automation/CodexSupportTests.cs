using System.Text.Json;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class CodexSupportTests
{
    private sealed class FakeCodexInstall : IDisposable
    {
        private readonly string? _previous;

        public FakeCodexInstall()
        {
            _previous = Environment.GetEnvironmentVariable("KITTYCLAW_CODEX_BIN");
            Environment.SetEnvironmentVariable("KITTYCLAW_CODEX_BIN", ResolveDotnetBinary());
            CodexCli.ResetForTests();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_CODEX_BIN", _previous);
            CodexCli.ResetForTests();
        }

        private static string ResolveDotnetBinary()
        {
            var host = Environment.ProcessPath;
            var directory = host is null ? null : Path.GetDirectoryName(host);
            var candidate = directory is null ? null
                : Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (candidate is not null && File.Exists(candidate)) return candidate;

            var path = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            return path.Select(entry => Path.Combine(entry, name)).First(File.Exists);
        }
    }

    private static AgentRun NewRun() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        ProjectSlug = "codex-test",
        TicketId = null,
        AgentName = "agent",
        SkillFile = "agent/SKILL.md",
        ConcurrencyGroup = "agent",
        StartedAt = DateTime.UtcNow,
    };

    private static void Map(string json, AgentRun run)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(CodexStreamAdapter.TryMap(document.RootElement, json, run));
    }

    [Fact]
    public void Routing_QualifiedCodexModel_UsesCodexAndRemovesQualifier()
    {
        using var codex = new FakeCodexInstall();
        var resolution = ModelRouting.Resolve("codex:gpt-5.6-sol", null);
        var target = resolution.ToTarget("codex:gpt-5.6-sol");

        Assert.Equal(CliProvider.Codex, target.Provider);
        Assert.Equal("gpt-5.6-sol", target.Model);
        Assert.Null(target.ValidationError);
    }

    [Theory]
    [InlineData("gpt-oss:20b")]
    [InlineData("gpt-5.6-sol")]
    public void Routing_UnqualifiedGptModel_RemainsAvailableToOllama(string model)
    {
        var target = ModelRouting.Resolve(model, "http://localhost:11434/")
            .ToTarget(model);

        Assert.Equal(CliProvider.Claude, target.Provider);
        Assert.Equal(model, target.Model);
        Assert.Equal(model, target.Environment["ANTHROPIC_MODEL"]);
    }

    [Theory]
    [InlineData("codex:gpt-5.6-sol", true)]
    [InlineData("CODEX:gpt-5.6-sol", true)]
    [InlineData("gpt-5.6-sol", false)]
    [InlineData("gpt-oss:20b", false)]
    public void IsCodexModel_RequiresExplicitQualifier(string model, bool expected) =>
        Assert.Equal(expected, CodexCli.IsCodexModel(model));

    [Fact]
    public async Task Backend_BuildsExecInvocationWithUnqualifiedModel()
    {
        using var codex = new FakeCodexInstall();
        var context = new AgentRunContext
        {
            ProjectSlug = "p",
            WorkspacePath = "w",
            AgentName = "a",
            SkillFile = "a/SKILL.md",
            Target = ModelRouting.Resolve("codex:gpt-5.6-sol", null)
                .ToTarget("codex:gpt-5.6-sol"),
        };

        var invocation = await AgentCliBackend.For(CliProvider.Codex)
            .BuildInvocationAsync(context, "prompt", "", false, CancellationToken.None);

        Assert.Equal(["exec", "--json", "--dangerously-bypass-approvals-and-sandbox",
            "--skip-git-repo-check", "--model", "gpt-5.6-sol", "-"], invocation.Arguments);
        Assert.True(invocation.WritePromptToStdin);
    }

    [Fact]
    public void StreamAdapter_MapsSessionMessageUsageAndCompletion()
    {
        var run = NewRun();
        Map("""{"type":"thread.started","thread_id":"thread-123"}""", run);
        Map("""{"type":"item.completed","item":{"type":"agent_message","text":"Done"}}""", run);
        Map("""{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":3,"output_tokens":4}}""", run);

        Assert.Equal("thread-123", run.SessionId);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "assistant" && e.Text.Contains("Done"));
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "result");
        Assert.Equal(10, run.InputTokens);
        Assert.Equal(3, run.CacheReadTokens);
        Assert.Equal(4, run.OutputTokens);
    }

    [Fact]
    public void SessionScopeKey_UsesCodexNamespace() =>
        Assert.Equal("codex:chat:agent",
            AgentRunner.SessionScopeKey("agent", "chat", CliProvider.Codex));

    [Theory]
    [InlineData("2026-07-30T14:57:39Z WARN codex_core::plugins: invalid manifest", "diagnostic")]
    [InlineData("Authentication failed", "stderr")]
    public void Backend_HidesOnlyStructuredCodexWarnings(string line, string expectedKind) =>
        Assert.Equal(expectedKind,
            AgentCliBackend.For(CliProvider.Codex).MapStderrKind(line));
}
