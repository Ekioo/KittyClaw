using System.Net;
using System.Net.Sockets;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AutomationRule = KittyClaw.Core.Automation.Automation;

namespace KittyClaw.Core.Tests.Automation;

/// <summary>
/// Tests for the httpRequest automation action (ticket #137): placeholder resolution, the
/// SSRF guard (loopback blocked unless AllowLocalTargets), scheme allowlist, timeout, chain
/// continuation/abort, and secret redaction in logs.
/// </summary>
public class ActionExecutorHttpRequestTests
{
    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, ex) + (ex is null ? "" : " | " + ex));
        }
    }

    private sealed record ReceivedRequest(string PathAndQuery, string Body, Dictionary<string, string> Headers);

    /// <summary>Minimal one-shot HTTP server on loopback.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public TaskCompletionSource<ReceivedRequest> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;

        public LoopbackServer(TimeSpan? delay = null)
        {
            if (delay is not null) ResponseDelay = delay.Value;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    var headers = ctx.Request.Headers.AllKeys
                        .Where(k => k is not null)
                        .ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "");
                    Received.TrySetResult(new ReceivedRequest(ctx.Request.Url!.PathAndQuery, body, headers));
                    if (ResponseDelay > TimeSpan.Zero) await Task.Delay(ResponseDelay, _cts.Token);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            }
            catch { /* listener stopped */ }
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed record Harness(ActionExecutor Executor, ProjectRuntime Runtime, TicketService Tickets, string Slug, CapturingLogger Log);

    private static async Task<Harness> BuildAsync(string dataDir)
    {
        var projects = new ProjectService(dataDir);
        var project = await projects.CreateProjectAsync("http-action-test");
        var workspace = projects.ResolveWorkspacePath(project);
        Directory.CreateDirectory(workspace);

        var members = new MemberService(projects);
        var labels = new LabelService(projects);
        var sessions = new SessionRegistry();
        var runs = new AgentRunRegistry();
        var runner = new AgentRunner(sessions, runs, new RunConcurrencyGate(4), NullLogger<AgentRunner>.Instance);
        var cost = new CostTracker();
        var loc = new LocalizationService(new AppSettingsService(dataDir));
        var tickets = new TicketService(projects, members);
        var log = new CapturingLogger();
        var executor = new ActionExecutor(
            tickets, members, labels, sessions, runs, runner, cost, loc, projects,
            new RunStateManager(runs, cost, tickets, NullLogger.Instance), log);

        var rt = new ProjectRuntime(project.Slug) { Workspace = workspace, Config = new AutomationConfig() };
        return new Harness(executor, rt, tickets, project.Slug, log);
    }

    private static AutomationRule Chain(params ActionSpec[] actions) => new()
    {
        Id = "http-chain",
        Trigger = new StatusChangeTriggerSpec { To = "Done" },
        Actions = actions.ToList(),
    };

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private static async Task<bool> HasMarkerCommentAsync(Harness h, int ticketId) =>
        (await h.Tickets.GetTicketAsync(h.Slug, ticketId))!.Comments.Any(c => c.Content == "after-http");

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_ResolvesPlaceholders_InUrlBodyAndHeaders()
    {
        using var tmp = new TempDir();
        await using var server = new LoopbackServer();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "Fix login", status: "Done");

        var spec = new HttpRequestActionSpec
        {
            Url = $"http://127.0.0.1:{server.Port}/hook?ticket={{ticketId}}",
            Body = "{\"title\": \"{ticketTitle}\", \"status\": \"{ticketStatus}\"}",
            Headers = { ["X-Ticket"] = "{ticketId}" },
            AllowLocalTargets = true,
        };
        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, Chain(spec), new TriggerFiring(ticket.Id, ticket.Title, ticket.Status), CancellationToken.None);

        var received = await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal($"/hook?ticket={ticket.Id}", received.PathAndQuery);
        Assert.Contains("\"Fix login\"", received.Body);
        Assert.Contains("\"Done\"", received.Body);
        Assert.Equal(ticket.Id.ToString(), received.Headers["X-Ticket"]);
    }

    [Fact]
    public async Task LoopbackTarget_IsBlockedByDefault_AndAbortsChainWhenConfigured()
    {
        using var tmp = new TempDir();
        await using var server = new LoopbackServer();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Done");

        var spec = new HttpRequestActionSpec
        {
            Url = $"http://127.0.0.1:{server.Port}/hook",
            AbortOnFailure = true,
            // AllowLocalTargets deliberately NOT set.
        };
        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, Chain(spec, new AddCommentActionSpec { Content = "after-http", Author = "bot" }),
            new TriggerFiring(ticket.Id, "T", "Done"), CancellationToken.None);

        // The guard refuses the connection: nothing reaches the server, the chain aborts.
        Assert.False(await WaitForAsync(() => Task.FromResult(server.Received.Task.IsCompleted), 2000));
        Assert.False(await HasMarkerCommentAsync(h, ticket.Id));
        Assert.True(await WaitForAsync(() => Task.FromResult(
            h.Log.Lines.Any(l => l.Contains("httpRequest failed")))), "expected a failure log line");
    }

    [Fact]
    public async Task NonHttpScheme_IsRefused_ChainContinuesWithoutAbortOnFailure()
    {
        using var tmp = new TempDir();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Done");

        var spec = new HttpRequestActionSpec { Url = "file:///etc/passwd", AbortOnFailure = false };
        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, Chain(spec, new AddCommentActionSpec { Content = "after-http", Author = "bot" }),
            new TriggerFiring(ticket.Id, "T", "Done"), CancellationToken.None);

        Assert.True(await WaitForAsync(() => HasMarkerCommentAsync(h, ticket.Id)),
            "chain should continue after a refused URL when AbortOnFailure is false");
        Assert.Contains(h.Log.Lines, l => l.Contains("invalid or non-http(s) URL"));
    }

    [Fact]
    public async Task SlowServer_TimesOut_WithoutHangingTheChain()
    {
        using var tmp = new TempDir();
        await using var server = new LoopbackServer(delay: TimeSpan.FromSeconds(10));
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Done");

        var spec = new HttpRequestActionSpec
        {
            Url = $"http://127.0.0.1:{server.Port}/slow",
            TimeoutSeconds = 1,
            AllowLocalTargets = true,
            AbortOnFailure = false,
        };
        var started = DateTime.UtcNow;
        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, Chain(spec, new AddCommentActionSpec { Content = "after-http", Author = "bot" }),
            new TriggerFiring(ticket.Id, "T", "Done"), CancellationToken.None);

        Assert.True(await WaitForAsync(() => HasMarkerCommentAsync(h, ticket.Id)),
            "chain should continue after a timeout when AbortOnFailure is false");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "timeout must not hang the chain");
        Assert.Contains(h.Log.Lines, l => l.Contains("timed out"));
    }

    [Fact]
    public async Task Logs_NeverContainHeaderValues_NorUrlPath()
    {
        using var tmp = new TempDir();
        await using var server = new LoopbackServer();
        var h = await BuildAsync(tmp.Path);
        var ticket = await h.Tickets.CreateTicketAsync(h.Slug, "T", status: "Done");

        var spec = new HttpRequestActionSpec
        {
            Url = $"http://127.0.0.1:{server.Port}/hooks/secret-path-token-abc",
            Headers = { ["Authorization"] = "Bearer secret-header-xyz" },
            AllowLocalTargets = true,
        };
        await h.Executor.ExecuteAutomationAsync(
            h.Runtime, Chain(spec), new TriggerFiring(ticket.Id, "T", "Done"), CancellationToken.None);
        await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await WaitForAsync(() => Task.FromResult(h.Log.Lines.Any(l => l.Contains("httpRequest")))));

        lock (h.Log.Lines)
        {
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains("secret-header-xyz"));
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains("secret-path-token-abc"));
        }
    }

    // ── SSRF guard unit tests ───────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("169.254.169.254", true)]   // cloud metadata
    [InlineData("169.254.1.1", true)]       // link-local
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("224.0.0.1", true)]         // multicast
    [InlineData("fe80::1", true)]           // v6 link-local
    [InlineData("::ffff:127.0.0.1", true)]  // v4-mapped loopback
    [InlineData("8.8.8.8", false)]
    [InlineData("140.82.121.4", false)]
    [InlineData("2606:4700::1111", false)]
    public void IsBlockedTarget_ClassifiesAddresses(string ip, bool blocked)
    {
        Assert.Equal(blocked, HttpActionClient.IsBlockedTarget(IPAddress.Parse(ip)));
    }
}
