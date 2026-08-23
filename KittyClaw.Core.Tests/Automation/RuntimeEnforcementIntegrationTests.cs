using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// End-to-end fail-closed enforcement through the real dispatch pipeline: mock claude CLI +
/// per-run PreToolUse hook bundle + a real-socket approvals gate. The hook subprocess is a
/// separate OS process, so these tests need Kestrel on a loopback port, not TestServer.
/// </summary>
[Collection("MockClaude")]
public sealed class RuntimeEnforcementIntegrationTests
{
    [Fact]
    public async Task ProjectSetting_IsAppliedCentrallyToEveryAgentRun()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("project-enforcement");
        await projects.UpdateProjectAsync(project.Slug, null,
            boundaryEnforcement: BoundaryEnforcementMode.Enforce);
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);
        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance, projects: projects);

        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "You are a test agent.",
            MaxTurns = 1,
            Provider = CliProvider.Codex,
        }, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains(run.SnapshotBuffer(), entry =>
            entry.Kind == "error" && entry.Text.Contains("Fail-closed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CliProvider.Codex)]
    [InlineData(CliProvider.Grok)]
    [InlineData(CliProvider.Mistral)]
    [InlineData(CliProvider.DeepSeek)]
    public async Task EnforcedDispatch_FailsClosedOnObservationOnlyProviders(CliProvider provider)
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync($"enforce-excluded-{provider}".ToLowerInvariant());
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var runner = new AgentRunner(new SessionRegistry(), new AgentRunRegistry(), new RunConcurrencyGate(1),
            NullLogger<AgentRunner>.Instance);
        var run = await runner.RunAsync(new AgentRunContext
        {
            ProjectSlug = project.Slug,
            WorkspacePath = workspace,
            AgentName = "test-agent",
            SkillFile = "(inline)",
            InlineSkillContent = "You are a test agent.",
            MaxTurns = 1,
            Provider = provider,
            BoundaryEnforcement = BoundaryEnforcementMode.Enforce,
        }, CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        var error = Assert.Single(run.SnapshotBuffer(), e => e.Kind == "error");
        Assert.Contains("Fail-closed", error.Text);
        foreach (var boundary in Enum.GetValues<BoundaryActionClass>())
            Assert.Contains(boundary.ToString(), error.Text);
    }

    [Fact]
    public async Task EnforcedOllamaDispatch_UsesClaudeHookAndBlocksProtectedEffectWithoutDecision()
    {
        using var harness = await EnforcementHarness.StartAsync("enforce-ollama");
        harness.WriteScenario("ollama-protected-effect",
            """{"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}""",
            """{"_meta":{"hooked_effect":{"tool_name":"Bash","tool_input":{"command":"git push origin main"},"write_file":{"path":"ollama-effect.txt","content":"executed"}}}}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":42,"num_turns":1}""");

        var run = await harness.RunAsync("ollama-protected-effect", deadlineSeconds: 2,
            new Dictionary<string, string>
            {
                ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:11434",
                ["ANTHROPIC_AUTH_TOKEN"] = "ollama",
            });

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.False(File.Exists(Path.Combine(harness.Workspace, "ollama-effect.txt")));
        var request = Assert.Single(await harness.Registry.QueryRequestsAsync(harness.Slug, new()));
        Assert.Equal("PushOrPullRequest", request.ActionClass);
        Assert.Empty(await harness.Registry.QueryReceiptsAsync(harness.Slug, request.RequestId));
    }

    [Fact]
    public async Task ClaudeHook_MissingRequiredEnvironment_DeniesBeforeEffect()
    {
        var bundle = RuntimeEnforcementHooks.WriteClaudeHookBundle();
        try
        {
            var settings = await File.ReadAllTextAsync(Path.Combine(bundle, RuntimeEnforcementHooks.SettingsFileName));
            using var document = System.Text.Json.JsonDocument.Parse(settings);
            var command = document.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0]
                .GetProperty("hooks")[0].GetProperty("command").GetString()!;
            var start = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("powershell.exe", command["powershell.exe ".Length..])
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", command["/bin/sh ".Length..]);
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.UseShellExecute = false;
            start.Environment.Remove("KITTYCLAW_API_URL");
            start.Environment.Remove("KITTYCLAW_PROJECT_SLUG");
            start.Environment.Remove("KITTYCLAW_RUN_ID");

            using var process = System.Diagnostics.Process.Start(start)!;
            await process.StandardInput.WriteAsync("""{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"git push origin main"}}""");
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\"permissionDecision\":\"deny\"", output);
            Assert.Contains("environment variables are missing", output);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public async Task EnforcedClaudeRun_ProtectedEffectNeverExecutesWithoutADecision()
    {
        using var harness = await EnforcementHarness.StartAsync("enforce-pending");
        harness.WriteScenario("enforced-push",
            """{"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}""",
            """{"_meta":{"hooked_effect":{"tool_name":"Bash","tool_input":{"command":"git push origin main"},"write_file":{"path":"protected-effect.txt","content":"executed"}}}}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":42,"num_turns":1}""");

        // Nobody ever decides: the hook's short deadline elapses, the gate finalizes to deny,
        // and the run completes without the protected effect.
        var run = await harness.RunAsync("enforced-push", deadlineSeconds: 2);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.False(File.Exists(Path.Combine(harness.Workspace, "protected-effect.txt")));
        var request = Assert.Single(await harness.Registry.QueryRequestsAsync(harness.Slug, new()));
        Assert.Equal("PushOrPullRequest", request.ActionClass);
        Assert.Equal("pending", request.State);
        Assert.Empty(await harness.Registry.QueryReceiptsAsync(harness.Slug, request.RequestId));
    }

    [Fact]
    public async Task EnforcedClaudeRun_AllowOncePermitsExactlyOneOfTwoIdenticalEffects()
    {
        using var harness = await EnforcementHarness.StartAsync("enforce-once");
        harness.WriteScenario("enforced-push-twice",
            """{"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}""",
            """{"_meta":{"hooked_effect":{"tool_name":"Bash","tool_input":{"command":"git push origin main"},"write_file":{"path":"effect-first.txt","content":"executed"}}}}""",
            """{"_meta":{"hooked_effect":{"tool_name":"Bash","tool_input":{"command":"git push origin main"},"write_file":{"path":"effect-second.txt","content":"executed"}}}}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":42,"num_turns":1}""");

        var runTask = harness.StartRunAsync("enforced-push-twice", deadlineSeconds: 25);
        var request = await harness.WaitForPendingRequestAsync();
        var now = DateTime.UtcNow;
        await harness.Workflow.DecideAsync(harness.Slug, new("decision-once", request.RequestId,
            ApprovalDecisionKind.AllowOnce, "owner", now, now.AddMinutes(5), "once", "approved", null));

        var run = await runTask.WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.True(File.Exists(Path.Combine(harness.Workspace, "effect-first.txt")));
        Assert.False(File.Exists(Path.Combine(harness.Workspace, "effect-second.txt")));
        var receipts = await harness.Registry.QueryReceiptsAsync(harness.Slug, request.RequestId);
        Assert.Single(receipts, r => r.Outcome == ApprovalReceiptOutcome.Allowed);
        Assert.Contains(receipts, r => r.Outcome == ApprovalReceiptOutcome.EffectSucceeded);
    }

    [Fact]
    public async Task EnforcedClaudeRun_DeniedDecisionBlocksTheEffect()
    {
        using var harness = await EnforcementHarness.StartAsync("enforce-denied");
        harness.WriteScenario("enforced-publish",
            """{"type":"system","subtype":"init","session_id":"{{session_id}}","model":"mock"}""",
            """{"_meta":{"hooked_effect":{"tool_name":"Bash","tool_input":{"command":"npm publish"},"write_file":{"path":"published.txt","content":"executed"}}}}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":42,"num_turns":1}""");

        var runTask = harness.StartRunAsync("enforced-publish", deadlineSeconds: 25);
        var request = await harness.WaitForPendingRequestAsync();
        var now = DateTime.UtcNow;
        await harness.Workflow.DecideAsync(harness.Slug, new("decision-deny", request.RequestId,
            ApprovalDecisionKind.Deny, "owner", now, now.AddMinutes(5), "once", "not allowed", null));

        var run = await runTask.WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.False(File.Exists(Path.Combine(harness.Workspace, "published.txt")));
        Assert.Contains(await harness.Registry.QueryReceiptsAsync(harness.Slug, request.RequestId),
            r => r.Outcome == ApprovalReceiptOutcome.Denied);
    }

    private sealed class EnforcementHarness : IDisposable
    {
        private readonly TempDir _tmp;
        private readonly WebApplication _app;
        private readonly string _scenarioDir;
        private readonly AgentRunner _runner;

        public string Slug { get; }
        public string Workspace { get; }
        public ApprovalRegistryService Registry { get; }
        public ApprovalWorkflowService Workflow { get; }

        private EnforcementHarness(TempDir tmp, WebApplication app, string scenarioDir, string slug,
            string workspace, AgentRunner runner, ApprovalRegistryService registry, ApprovalWorkflowService workflow)
        {
            _tmp = tmp;
            _app = app;
            _scenarioDir = scenarioDir;
            _runner = runner;
            Slug = slug;
            Workspace = workspace;
            Registry = registry;
            Workflow = workflow;
        }

        public static async Task<EnforcementHarness> StartAsync(string projectName)
        {
            var tmp = new TempDir();
            var projects = new ProjectService(tmp.Path);
            var project = await projects.CreateProjectAsync(projectName);
            var workspace = projects.ResolveWorkspacePath(project);
            Directory.CreateDirectory(workspace);
            var scenarioDir = Path.Combine(tmp.Path, "mock-scenarios");
            Directory.CreateDirectory(scenarioDir);

            var runs = new AgentRunRegistry();
            var registry = new ApprovalRegistryService(projects);
            var workflow = new ApprovalWorkflowService(registry, runs);
            var gate = new RuntimeBoundaryGateService(
                new RuntimeBoundaryEnforcementService(registry), workflow, runs,
                new BoundaryObservationService(projects, NullLogger<BoundaryObservationService>.Instance));
            var runner = new AgentRunner(new SessionRegistry(), runs, new RunConcurrencyGate(1),
                NullLogger<AgentRunner>.Instance);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapPost("/api/projects/{slug}/approvals/gate",
                async (string slug, string runId, bool? finalize, HttpRequest request) =>
                {
                    using var reader = new StreamReader(request.Body);
                    var payload = await reader.ReadToEndAsync();
                    return Results.Ok(await gate.EvaluateHookAsync(slug, runId, payload, finalize ?? false));
                });
            await app.StartAsync();

            return new(tmp, app, scenarioDir, project.Slug, workspace, runner, registry, workflow);
        }

        public void WriteScenario(string name, params string[] lines)
        {
            File.WriteAllText(Path.Combine(_scenarioDir, $"{name}.ndjson"), string.Join('\n', lines));
            TestSkillBuilder.Create(Workspace, "test-agent", scenario: name);
        }

        public Task<AgentRun> StartRunAsync(string scenario, int deadlineSeconds,
            IReadOnlyDictionary<string, string>? extraEnvironment = null) =>
            _runner.RunAsync(new AgentRunContext
            {
                ProjectSlug = Slug,
                WorkspacePath = Workspace,
                AgentName = "test-agent",
                SkillFile = "test-agent/SKILL.md",
                MaxTurns = 1,
                TicketId = 170,
                BoundaryEnforcement = BoundaryEnforcementMode.Enforce,
                MaxRunDuration = TimeSpan.FromSeconds(120),
                Env = MergeEnvironment(new Dictionary<string, string>
                {
                    ["KITTYCLAW_MOCK_SCENARIOS_DIR"] = _scenarioDir,
                    ["KITTYCLAW_API_URL"] = _app.Urls.First().TrimEnd('/'),
                    ["KITTYCLAW_ENFORCEMENT_POLL_SECONDS"] = "1",
                    ["KITTYCLAW_ENFORCEMENT_DEADLINE_SECONDS"] = deadlineSeconds.ToString(),
                }, extraEnvironment),
            }, CancellationToken.None);

        public async Task<AgentRun> RunAsync(string scenario, int deadlineSeconds,
            IReadOnlyDictionary<string, string>? extraEnvironment = null) =>
            await StartRunAsync(scenario, deadlineSeconds, extraEnvironment).WaitAsync(TimeSpan.FromSeconds(90));

        private static Dictionary<string, string> MergeEnvironment(Dictionary<string, string> environment,
            IReadOnlyDictionary<string, string>? extraEnvironment)
        {
            if (extraEnvironment is not null)
                foreach (var pair in extraEnvironment) environment[pair.Key] = pair.Value;
            return environment;
        }

        public async Task<ApprovalRequestRecord> WaitForPendingRequestAsync()
        {
            for (var i = 0; i < 300; i++)
            {
                var pending = (await Registry.QueryRequestsAsync(Slug, new()))
                    .FirstOrDefault(r => r.State == "pending");
                if (pending is not null) return pending;
                await Task.Delay(100);
            }
            throw new TimeoutException("No pending approval request appeared.");
        }

        public void Dispose()
        {
            try { _app.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _tmp.Dispose();
        }
    }
}
