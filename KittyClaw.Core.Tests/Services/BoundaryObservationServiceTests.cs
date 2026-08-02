using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Services;

public sealed class BoundaryObservationServiceTests
{
    public static TheoryData<string, string, BoundaryActionClass> BoundaryCases => new()
    {
        { "Bash", "{\"command\":\"git push origin feature\"}", BoundaryActionClass.PushOrPullRequest },
        { "shell_command", "{\"command\":\"npm publish\"}", BoundaryActionClass.PublishOrDeploy },
        { "bash", "{\"command\":\"curl https://new.example/api\"}", BoundaryActionClass.NewNetworkDestination },
        { "Read", "{\"path\":\"C:/repo/.env\"}", BoundaryActionClass.SecretAccess },
        { "shell", "{\"command\":\"git reset --hard HEAD~1\"}", BoundaryActionClass.DestructiveOperation },
    };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public async Task Observe_ClassifiesSupportedBoundaryFamilies(string tool, string detail, BoundaryActionClass expected)
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("boundary");

        service.RecordRun(run);
        service.Observe(run, new(DateTime.UtcNow, "tool_use", tool, detail));

        var observation = Assert.Single(await service.QueryAsync("project"));
        Assert.Equal(expected, observation.Classification);
        Assert.Equal(64, observation.ArgumentsHash.Length);
        Assert.DoesNotContain("origin feature", observation.ResourceDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrdinaryLocalRuns_StayBelowOnePotentialRequestPerTwentyRuns()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        service.Observe(Run("known-idn-baseline"), new(DateTime.UtcNow.AddMinutes(-1), "tool_use", "Bash",
            "{\"command\":\"curl https://bücher.example/bootstrap\"}"));
        for (var i = 0; i < 20; i++)
        {
            var run = Run($"ordinary-{i}");
            service.RecordRun(run);
            service.Observe(run, new(DateTime.UtcNow.AddSeconds(i), "tool_use", "shell_command", "{\"command\":\"dotnet test\"}"));
            service.Observe(run, new(DateTime.UtcNow.AddSeconds(i), "tool_use", "Read", "{\"path\":\"README.md\"}"));
            var localUrl = (i % 8) switch
            {
                0 => "http://localhost:5230/api/projects",
                1 => "http://localhost.:5230/api/projects",
                2 => "http://[::1]:5230/api/projects",
                3 => "http://[fc00::1]:5230/api/projects",
                4 => "http://[fe80::1]:5230/api/projects",
                5 => "http://169.254.1.10:5230/api/projects",
                6 => "http://0.0.0.0:5230/api/projects",
                _ => "http://[::]:5230/api/projects",
            };
            service.Observe(run, new(DateTime.UtcNow.AddSeconds(i), "tool_use", "Bash", $"{{\"command\":\"curl {localUrl}\"}}"));
            var knownIdnUrl = i % 2 == 0 ? "https://bücher.example/v1" : "https://xn--bcher-kva.example/v2";
            service.Observe(run, new(DateTime.UtcNow.AddSeconds(i), "tool_use", "Bash", $"{{\"command\":\"curl {knownIdnUrl}\"}}"));
        }

        var metrics = await service.MetricsAsync("project");
        Assert.Equal(20, metrics.RunCount);
        Assert.Equal(0, metrics.PotentialRequestCount);
        Assert.True(metrics.MeetsOrdinaryRunTarget);
    }

    [Fact]
    public async Task RootDottedLocalhost_IsNotObservedAsNewDestination()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("root-dotted-localhost");
        service.RecordRun(run);

        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"curl http://localhost.:5230/api/projects\"}"));

        Assert.Empty(await service.QueryAsync("project"));
    }

    [Fact]
    public async Task Ipv6Loopback_IsNotObservedAsNewDestination()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("ipv6-loopback");
        service.RecordRun(run);

        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"curl http://[::1]:5230/api/projects\"}"));

        Assert.Empty(await service.QueryAsync("project"));
    }

    [Theory]
    [InlineData("fc00::1")]
    [InlineData("fdff:ffff::1")]
    [InlineData("fe80::1")]
    [InlineData("febf:ffff::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.1.20")]
    public async Task PrivateIpv6Destinations_AreNotObservedAsNew(string host)
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("private-ipv6");
        service.RecordRun(run);

        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", $"{{\"command\":\"curl http://[{host}]:5230/api/projects\"}}"));

        Assert.Empty(await service.QueryAsync("project"));
    }

    [Theory]
    [InlineData("169.254.1.10")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task AdditionalLocalAddressForms_AreNotObservedAsNew(string host)
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("additional-local-address");
        service.RecordRun(run);
        var url = host.Contains(':') ? $"http://[{host}]:5230/api/projects" : $"http://{host}:5230/api/projects";

        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", $"{{\"command\":\"curl {url}\"}}"));

        Assert.Empty(await service.QueryAsync("project"));
    }

    [Fact]
    public async Task NetworkDestination_IsObservedOnlyOnFirstExternalUse()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var first = Run("network-first");
        var repeated = Run("network-repeated");
        service.RecordRun(first);
        service.RecordRun(repeated);

        service.Observe(first, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"curl https://api.example.test/v1\"}"));
        service.Observe(repeated, new(DateTime.UtcNow.AddMinutes(1), "tool_use", "Bash", "{\"command\":\"curl https://api.example.test/v2\"}"));

        var observation = Assert.Single(await service.QueryAsync("project"));
        Assert.Equal("api.example.test", observation.ResourceDisplay);
        Assert.Equal("network-first", observation.RunId);
    }

    [Fact]
    public async Task RootDottedAndCanonicalExternalHosts_ShareBaselineEntry()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var dotted = Run("network-dotted");
        var canonical = Run("network-canonical");
        service.RecordRun(dotted);
        service.RecordRun(canonical);

        service.Observe(dotted, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"curl https://api.example.test./v1\"}"));
        service.Observe(canonical, new(DateTime.UtcNow.AddMinutes(1), "tool_use", "Bash", "{\"command\":\"curl https://api.example.test/v2\"}"));

        var observation = Assert.Single(await service.QueryAsync("project"));
        Assert.Equal("api.example.test", observation.ResourceDisplay);
        Assert.Equal("network-dotted", observation.RunId);
    }

    [Fact]
    public async Task UnicodeAndPunycodeExternalHosts_ShareBaselineEntry()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var unicode = Run("network-unicode");
        var punycode = Run("network-punycode");
        service.RecordRun(unicode);
        service.RecordRun(punycode);

        service.Observe(unicode, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"curl https://bücher.example/v1\"}"));
        service.Observe(punycode, new(DateTime.UtcNow.AddMinutes(1), "tool_use", "Bash", "{\"command\":\"curl https://xn--bcher-kva.example./v2\"}"));

        var observation = Assert.Single(await service.QueryAsync("project"));
        Assert.Equal("xn--bcher-kva.example", observation.ResourceDisplay);
        Assert.Equal("network-unicode", observation.RunId);
    }

    [Fact]
    public async Task GitGlobalOptions_DoNotHidePush()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("git-options");
        service.RecordRun(run);

        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"git -C C:/repo -c credential.helper= push origin main\"}"));

        Assert.Equal(BoundaryActionClass.PushOrPullRequest,
            Assert.Single(await service.QueryAsync("project")).Classification);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task ToolResult_UpdatesCorrelatedObservationOutcome(string outcome)
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run($"outcome-{outcome}");
        service.RecordRun(run);
        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"git push origin main\"}", "tool-42"));

        service.Observe(run, new(DateTime.UtcNow.AddSeconds(1), "tool_result", outcome, "{}", "tool-42"));

        Assert.Equal(outcome, Assert.Single(await service.QueryAsync("project")).Outcome);
    }

    [Fact]
    public async Task TerminalResult_ProvidesFallbackOutcomeWithoutCorrelationId()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("fallback-outcome");
        service.RecordRun(run);
        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"git push origin main\"}"));

        service.Observe(run, new(DateTime.UtcNow.AddSeconds(1), "result", "[result]", "{\"is_error\":false}"));

        Assert.Equal("succeeded", Assert.Single(await service.QueryAsync("project")).Outcome);
    }

    [Fact]
    public async Task Review_ProducesAuditableFalsePositiveMetrics()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("reviewed");
        service.RecordRun(run);
        service.Observe(run, new(DateTime.UtcNow, "tool_use", "Bash", "{\"command\":\"git push origin main\"}"));
        var observation = Assert.Single(await service.QueryAsync("project"));

        await service.ReviewAsync("project", observation.ObservationId, true);

        var metrics = await service.MetricsAsync("project", 1);
        Assert.Equal(1, metrics.ReviewedCount);
        Assert.Equal(1, metrics.FalsePositiveCount);
        Assert.Equal(1d, metrics.ReviewedFalsePositiveRate);
    }

    [Fact]
    public async Task NonToolAndUnknownEvents_AreIgnored()
    {
        using var temp = new TempDir();
        var service = Create(temp);
        var run = Run("ignored");
        service.RecordRun(run);
        service.Observe(run, new(DateTime.UtcNow, "assistant", "git push", "{\"command\":\"git push\"}"));
        service.Observe(run, new(DateTime.UtcNow, "tool_use", "shell", "{\"command\":\"git status\"}"));

        Assert.Empty(await service.QueryAsync("project"));
    }

    private static BoundaryObservationService Create(TempDir temp) =>
        new(new ProjectService(temp.Path), NullLogger<BoundaryObservationService>.Instance);

    private static AgentRun Run(string id) => new()
    {
        RunId = id,
        ProjectSlug = "project",
        TicketId = 168,
        AgentName = "programmer",
        SkillFile = "programmer/SKILL.md",
        ConcurrencyGroup = "programmer",
        StartedAt = DateTime.UtcNow,
        CliVersion = new("codex", "codex", "1", "1", "ok", null, false),
    };
}
