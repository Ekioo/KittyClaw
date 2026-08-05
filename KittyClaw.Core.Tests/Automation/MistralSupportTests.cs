using System.Text.Json;
using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class MistralSupportTests
{
    private sealed class FakeMistralInstall : IDisposable
    {
        private readonly string? _previous;

        public FakeMistralInstall()
        {
            _previous = Environment.GetEnvironmentVariable("KITTYCLAW_MISTRAL_BIN");
            Environment.SetEnvironmentVariable("KITTYCLAW_MISTRAL_BIN", ResolveDotnetBinary());
            MistralCli.ResetForTests();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_MISTRAL_BIN", _previous);
            MistralCli.ResetForTests();
        }

        private static string ResolveDotnetBinary()
        {
            var host = Environment.ProcessPath;
            var directory = host is null ? null : Path.GetDirectoryName(host);
            var candidate = directory is null ? null
                : Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (candidate is not null && File.Exists(candidate)) return candidate;
            var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            return (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => Path.Combine(entry, name)).First(File.Exists);
        }
    }

    private static AgentRun NewRun() => new()
    {
        RunId = Guid.NewGuid().ToString("N"), ProjectSlug = "mistral-test",
        TicketId = null, AgentName = "agent", SkillFile = "agent/SKILL.md",
        ConcurrencyGroup = "agent", StartedAt = DateTime.UtcNow,
        Model = "mistral-medium-3.5",
    };

    [Fact]
    public void Routing_QualifiedModel_UsesVibeAliasAndEnvironment()
    {
        using var install = new FakeMistralInstall();
        var target = ModelRouting.Resolve("mistral:mistral-medium-3.5", null)
            .ToTarget("mistral:mistral-medium-3.5");

        Assert.Equal(CliProvider.Mistral, target.Provider);
        Assert.Equal("mistral-medium-3.5", target.Model);
        Assert.Equal("mistral-medium-3.5", target.Environment["VIBE_ACTIVE_MODEL"]);
        Assert.Null(target.ValidationError);
    }

    [Fact]
    public async Task Backend_UsesProgrammaticStreamingAndNativeResume()
    {
        using var install = new FakeMistralInstall();
        var context = new AgentRunContext
        {
            ProjectSlug = "p", WorkspacePath = "w", AgentName = "a",
            SkillFile = "a/SKILL.md", MaxTurns = 12,
            Target = ModelRouting.Resolve("mistral:devstral-small", null)
                .ToTarget("mistral:devstral-small"),
        };

        var invocation = await AgentCliBackend.For(CliProvider.Mistral)
            .BuildInvocationAsync(context, "prompt", "session-123", true, CancellationToken.None);

        Assert.Equal(["--prompt", "--max-turns", "12", "--output", "streaming",
            "--agent", "auto-approve", "--trust", "--resume", "session-123"], invocation.Arguments);
        Assert.True(invocation.WritePromptToStdin);
    }

    [Fact]
    public void StreamAdapter_MapsSessionAssistantAndEffect()
    {
        var run = NewRun();
        Map("""{"id":"1","sessionId":"vibe-123","type":"message","role":"assistant","content":[{"type":"text","text":"Terminé"}]}""", run);
        Map("""{"id":"2","sessionId":"vibe-123","type":"effect","title":"shell","detail":{"command":"dotnet test"},"state":{"status":"completed"}}""", run);

        Assert.Equal("vibe-123", run.SessionId);
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "assistant" && e.Text.Contains("Terminé"));
        Assert.Contains(run.SnapshotBuffer(), e => e.Kind == "tool_use" && e.Text == "shell");
    }

    [Fact]
    public void StreamAdapter_DoesNotReplayOldSessionHistory()
    {
        var run = NewRun();
        var old = new DateTimeOffset(run.StartedAt.AddMinutes(-1)).ToUnixTimeMilliseconds();
        Map($$"""{"id":"old","sessionId":"vibe-123","createdAt":{{old}},"type":"message","role":"assistant","content":[{"type":"text","text":"ancien"}]}""", run);

        Assert.Equal("vibe-123", run.SessionId);
        Assert.Empty(run.SnapshotBuffer());
    }

    [Fact]
    public void SessionScopeAndEffectiveId_UseMistralNamespace()
    {
        Assert.Equal("mistral:chat:agent", AgentRunner.SessionScopeKey("agent", "chat", CliProvider.Mistral));
        Assert.Equal("reported", AgentRunner.ResolveEffectiveSessionId(
            CliProvider.Mistral, "provisional", "reported"));
    }

    private static void Map(string json, AgentRun run)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(MistralStreamAdapter.TryMap(document.RootElement, json, run));
    }
}
