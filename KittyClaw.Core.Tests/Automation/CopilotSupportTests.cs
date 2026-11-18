using System.Diagnostics;
using KittyClaw.Core.Automation;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Automation;

[Collection("MockClaude")]
public sealed class CopilotSupportTests
{
    [Fact]
    public async Task Catalog_includes_cli_default_and_current_model_families()
    {
        Assert.Contains("copilot:default", CopilotCli.Models);
        Assert.Contains("copilot:gpt-6.2-astra", CopilotCli.Models);
        Assert.Contains("copilot:claude-sonnet-5.5", CopilotCli.Models);
        Assert.Contains("copilot:gemini-4-argon", CopilotCli.Models);
        Assert.Null(CopilotCli.ToCliModel("copilot:default"));
        Assert.Equal("gpt-6.2-astra", CopilotCli.ToCliModel("copilot:gpt-6.2-astra"));
    }

    [Fact]
    public void Routing_qualifiedCopilotModel_UsesCopilotAndRemovesQualifier()
    {
        const string model = "copilot:gpt-6.2-astra";

        var resolution = ModelRouting.Resolve(model, null);

        if (resolution.Error is not null)
            Assert.Contains("GitHub Copilot", resolution.Error, StringComparison.OrdinalIgnoreCase);
        else
        {
            var target = resolution.ToTarget(model);
            Assert.Equal(CliProvider.Copilot, target.Provider);
            Assert.Equal("gpt-6.2-astra", target.Model);
        }
    }

    [Fact]
    public async Task Backend_usesNonInteractiveSessionModelAttachmentAndSecretRedactionOptions()
    {
        const string secretValue = "must-not-appear-in-arguments";
        var context = new AgentRunContext
        {
            ProjectSlug = "p",
            WorkspacePath = "w",
            AgentName = "a",
            SkillFile = "a/SKILL.md",
            ImagePaths = ["image.png"],
            Target = new AgentDispatchTarget("gpt-6.2-astra", CliProvider.Copilot,
                new Dictionary<string, string> { ["SAMPLE_SECRET"] = secretValue }),
        };

        var invocation = await AgentCliBackend.For(CliProvider.Copilot)
            .BuildInvocationAsync(context, "prompt", "session-id", true, CancellationToken.None);

        Assert.True(invocation.WritePromptToStdin);
        Assert.Contains("--silent", invocation.Arguments);
        Assert.Contains("--no-ask-user", invocation.Arguments);
        Assert.Contains("--no-auto-update", invocation.Arguments);
        Assert.Contains("--allow-all-tools", invocation.Arguments);
        Assert.Contains("--model", invocation.Arguments);
        Assert.Contains("gpt-6.2-astra", invocation.Arguments);
        Assert.Contains("--session-id", invocation.Arguments);
        Assert.Contains("session-id", invocation.Arguments);
        Assert.Contains("--attachment=image.png", invocation.Arguments);
        Assert.Contains("--secret-env-vars=SAMPLE_SECRET", invocation.Arguments);
        Assert.DoesNotContain(secretValue, invocation.Arguments);
        Assert.DoesNotContain("--resume", invocation.Arguments);
    }

    [Fact]
    public async Task Backend_omitsModelArgumentForCopilotCliDefault()
    {
        var context = new AgentRunContext
        {
            ProjectSlug = "p",
            WorkspacePath = "w",
            AgentName = "a",
            SkillFile = "a/SKILL.md",
            Target = new AgentDispatchTarget("copilot:default", CliProvider.Copilot,
                new Dictionary<string, string>()),
        };

        var invocation = await AgentCliBackend.For(CliProvider.Copilot)
            .BuildInvocationAsync(context, "prompt", "session-id", false, CancellationToken.None);

        Assert.DoesNotContain("--model", invocation.Arguments);
        Assert.Contains("session-id", invocation.Arguments);
    }

    [Fact]
    public async Task PlainTextOutput_preservesJsonAndMultilineAssistantResponse()
    {
        var startInfo = SyntheticProcessFixture.Command(
            "[Console]::WriteLine('{\"type\":\"assistant\"}'); [Console]::WriteLine('second line')",
            "printf '%s\\n' '{\"type\":\"assistant\"}' 'second line'");
        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());

        var run = new AgentRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            ProjectSlug = "copilot-test",
            TicketId = null,
            AgentName = "agent",
            SkillFile = "agent/SKILL.md",
            ConcurrencyGroup = "agent",
            StartedAt = DateTime.UtcNow,
        };
        await AgentStreamPump.PumpStdoutAsync(
            process, run, CliProvider.Copilot, CancellationToken.None);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var events = run.SnapshotBuffer();
        Assert.Equal("{\"type\":\"assistant\"}\nsecond line",
            string.Concat(events.Where(e => e.Kind == "content_block_delta").Select(e => e.Text)));
        Assert.Contains(events, e => e.Kind == "assistant"
            && e.Text == "[assistant] {\"type\":\"assistant\"}\nsecond line");
    }

    [Fact]
    public void SessionScopeKey_UsesCopilotNamespace() =>
        Assert.Equal("copilot:chat:agent",
            AgentRunner.SessionScopeKey("agent", "chat", CliProvider.Copilot));
}
