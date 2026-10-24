using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using KittyClaw.Web.Markdown;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace KittyClaw.Core.Tests.Web;

// Security regressions use isolated data; no production project or real provider can run.
public sealed class RemoteSecurityRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public void Failed_codes_are_limited_per_source_without_blocking_another_client()
    {
        using var dir = new TempDir();
        var access = new NetworkAccessService(dir.Path, new EphemeralDataProtectionProvider(), 5233);
        var enrollment = access.CreateEnrollment();
        var key = Decode(access.EnrollmentSecret(enrollment));
        var now = DateTimeOffset.UtcNow;
        Assert.True(access.Enable(enrollment, Code(key, now.AddSeconds(-30))));
        for (var i = 0; i < 5; i++) Assert.Null(access.Authenticate("invalid", now, "attacker"));
        Assert.Null(access.Authenticate(Code(key, now), now, "attacker"));
        Assert.NotNull(access.Authenticate(Code(key, now), now, "owner"));
        Assert.NotNull(access.Authenticate(Code(key, now.AddSeconds(61)), now.AddSeconds(61), "attacker"));
        output.WriteLine("Source throttling rejects the attacker while another client can authenticate.");
    }

    [Fact]
    public async Task Logout_revokes_captured_cookie_and_persists_revocation_without_signing_out_another_client()
    {
        using var fixture = new GateFixture();
        var now = DateTimeOffset.UtcNow;
        var session = fixture.Access.Authenticate(Code(fixture.Key, now), now);
        Assert.NotNull(session);
        var otherSession = fixture.Access.Authenticate(Code(fixture.Key, now.AddSeconds(30)), now.AddSeconds(30), "other-client");
        Assert.NotNull(otherSession);
        var login = await fixture.Client.GetAsync("https://audit.kittyclaw.dev/access");
        var html = await login.Content.ReadAsStringAsync();
        var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value);
        Assert.NotEmpty(csrf);
        var cookies = "__Host-KittyClawAccess=" + session + "; " + string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        var revocations = 0;
        var individualRevocations = new List<string>();
        fixture.Access.SessionsRevoked += () => revocations++;
        fixture.Access.SessionRevoked += individualRevocations.Add;
        using var logout = new HttpRequestMessage(HttpMethod.Post, "https://audit.kittyclaw.dev/access/logout");
        logout.Headers.Add("Cookie", cookies);
        logout.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf });
        var response = await fixture.Client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-KittyClawAccess=;"));
        using var replay = new HttpRequestMessage(HttpMethod.Get, "https://audit.kittyclaw.dev/settings");
        replay.Headers.Add("Cookie", "__Host-KittyClawAccess=" + session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.SendAsync(replay)).StatusCode);
        Assert.False(fixture.Access.ValidSession(session, now.AddHours(11)));
        Assert.True(fixture.Access.ValidSession(otherSession, now));
        Assert.Equal(NetworkAccessService.SessionKey(session!), Assert.Single(individualRevocations));
        var restored = new NetworkAccessService(fixture.DataDirectory, fixture.Protection, 5233);
        Assert.False(restored.ValidSession(session, now));
        Assert.True(restored.ValidSession(otherSession, now));
        Assert.Equal(0, revocations);
        output.WriteLine("Logout revokes the copied cookie across reloads; other sessions remain valid and the connector is not restarted.");
    }

    [Fact]
    public async Task Cross_origin_simple_POST_cannot_change_a_local_project_but_local_automation_still_works()
    {
        using var dir = new TempDir();
        var changes = new Dictionary<string, string?> { ["KITTYCLAW_DATA_DIR"] = dir.Path, ["KITTYCLAW_DESKTOP"] = "false", ["ASPNETCORE_URLS"] = "http://127.0.0.1:0" };
        var before = changes.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        foreach (var pair in changes) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        try
        {
            await using var factory = new Factory();
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
            var spec = await client.GetStringAsync("/openapi/v1.json");
            Assert.Contains("/api/projects/{slug}/pause", spec);
            var projects = factory.Services.GetRequiredService<ProjectService>();
            var project = await projects.CreateProjectAsync("Security Audit Synthetic");
            Assert.False(project.IsPaused);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects/" + project.Slug + "/pause");
            request.Headers.Add("Origin", "https://attacker.example");
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["ignored"] = "1" });
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False((await projects.GetProjectAsync(project.Slug))!.IsPaused);
            using var automation = new HttpRequestMessage(HttpMethod.Post, "/api/projects/" + project.Slug + "/pause");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(automation)).StatusCode);
            Assert.True((await projects.GetProjectAsync(project.Slug))!.IsPaused);
            output.WriteLine("Cross-origin form blocked; headerless local automation still works with synthetic data and all hosted services removed.");
        }
        finally { foreach (var pair in before) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    [Fact]
    public async Task All_Markdown_renderers_reject_active_attributes_and_keep_rich_formatting()
    {
        var payload = "![audit](https://audit.invalid/probe.png){onerror=\"window.__kittyclawAuditXss=1\"}";
        var renderers = new Dictionary<string, Func<string, string>>
        {
            ["chat"] = ChatMarkdownRenderer.Render,
            ["comments"] = value => Markdig.Markdown.ToHtml(value, CommentMarkdownPipeline.Build()),
            ["dashboard"] = value => TileRenderer.RenderByExtension(value, "audit.md")
        };
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "msedge" });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => route.AbortAsync());
        foreach (var (name, render) in renderers)
        {
            await page.EvaluateAsync("window.__kittyclawAuditXss = 0");
            await page.SetContentAsync(render(payload));
            Assert.False(await page.EvaluateAsync<bool>("window.__kittyclawAuditXss === 1"));
            Assert.False(await page.EvaluateAsync<bool>("[...document.querySelectorAll('*')].some(e => [...e.attributes].some(a => a.name.startsWith('on')))"));
            Assert.Contains("<strong>safe</strong>", render("**safe**"));
            Assert.Contains("<table>", render("| A | B |\n|---|---|\n| 1 | 2 |"));
            output.WriteLine(name + ": no script execution; formatting preserved.");
        }
    }

    [Theory]
    [InlineData("http://localhost", "http://localhost", "same-origin", "POST", false)]
    [InlineData("http://localhost", "https://attacker.example", "cross-site", "POST", true)]
    [InlineData("http://localhost", "null", "cross-site", "POST", true)]
    [InlineData("http://localhost", null, "cross-site", "POST", true)]
    [InlineData("http://localhost", null, "same-site", "POST", true)]
    [InlineData("http://localhost", "https://attacker.example", "cross-site", "GET", true)]
    [InlineData("http://localhost", null, "cross-site", "GET", false)]
    [InlineData("http://localhost", null, null, "POST", false)]
    public void Browser_origin_guard_preserves_local_navigation_and_automation(string url, string? origin, string? site, string method, bool blocked)
    {
        var uri = new Uri(url);
        var context = new DefaultHttpContext();
        context.Request.Scheme = uri.Scheme;
        context.Request.Host = new HostString(uri.Host);
        context.Request.Method = method;
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (site is not null) context.Request.Headers["Sec-Fetch-Site"] = site;
        Assert.Equal(blocked, NetworkAccessMiddleware.IsCrossOriginBrowserRequest(context));
    }

    [Theory]
    [InlineData(false, "198.51.100.10", "peer:192.0.2.10")]
    [InlineData(true, "198.51.100.10", "tunnel:198.51.100.10")]
    [InlineData(true, "::ffff:198.51.100.10", "tunnel:198.51.100.10")]
    [InlineData(true, "invalid", "peer:192.0.2.10")]
    public void Only_the_tunnel_boundary_trusts_Cloudflare_client_addresses(bool tunnel, string edge, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers["CF-Connecting-IP"] = edge;
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.50";
        if (tunnel) context.Features.Set(new RemoteTunnelRequest());
        Assert.Equal(expected, NetworkAccessMiddleware.LoginSource(context));
    }

    [Fact]
    public async Task Revoking_a_session_aborts_only_its_active_transport()
    {
        using var fixture = new GateFixture();
        var now = DateTimeOffset.UtcNow;
        var first = fixture.Access.Authenticate(Code(fixture.Key, now), now)!;
        var other = fixture.Access.Authenticate(Code(fixture.Key, now.AddSeconds(30)), now.AddSeconds(30))!;
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("audit.kittyclaw.dev");
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers.Cookie = "__Host-KittyClawAccess=" + first;
        var lifetime = new RequestLifetime();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(lifetime);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new NetworkAccessMiddleware(async _ => { entered.SetResult(); await finish.Task; });
        var request = middleware.InvokeAsync(context, fixture.Access, null!, null!);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            fixture.Access.RevokeSession("invalid-cookie", now);
            fixture.Access.RevokeSession(other, now);
            Assert.False(lifetime.Aborted);
            fixture.Access.RevokeSession(first, now);
            Assert.True(lifetime.Aborted);
        }
        finally { finish.SetResult(); await request; }
    }

    [Fact]
    public void Existing_cookie_and_state_schema_survive_upgrade_until_explicit_logout()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        var protector = protection.CreateProtector("KittyClaw.NetworkAccess.v1");
        var now = DateTimeOffset.UtcNow;
        var legacyState = System.Text.Json.JsonSerializer.Serialize(new
        {
            Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20)),
            LastStep = -1L, Generation = "existing-generation"
        });
        File.WriteAllText(Path.Combine(dir.Path, "network-access.json"), protector.Protect(legacyState));
        var legacyCookie = protector.Protect(System.Text.Json.JsonSerializer.Serialize(new
        {
            Generation = "existing-generation", Expires = now.AddHours(1)
        }));
        var access = new NetworkAccessService(dir.Path, protection, 5233);
        Assert.True(access.Enabled);
        Assert.True(access.ValidSession(legacyCookie, now));
        access.RevokeSession(legacyCookie, now);
        var restored = new NetworkAccessService(dir.Path, protection, 5233);
        Assert.True(restored.Enabled);
        Assert.False(restored.ValidSession(legacyCookie, now));
    }

    private sealed class RequestLifetime : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private static byte[] Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(value.Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }
    private static string Code(byte[] key, DateTimeOffset now) => NetworkAccessService.Totp(key, now.ToUnixTimeSeconds() / 30);
    private sealed class Factory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
            });
        }
    }
    private sealed class GateFixture : IDisposable
    {
        private readonly TempDir dir = new();
        private readonly IHost host;
        public NetworkAccessService Access { get; }
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public string DataDirectory => dir.Path;
        public byte[] Key { get; }
        public HttpClient Client { get; }
        public GateFixture()
        {
            Access = new NetworkAccessService(dir.Path, Protection, 5233);
            var enrollment = Access.CreateEnrollment();
            Key = Decode(Access.EnrollmentSecret(enrollment));
            Assert.True(Access.Enable(enrollment, Code(Key, DateTimeOffset.UtcNow.AddSeconds(-30))));
            host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            {
                services.AddLogging(); services.AddDataProtection().UseEphemeralDataProtectionProvider(); services.AddAntiforgery();
                services.AddSingleton(Access);
                services.AddSingleton(new RemoteTunnelOptions(true, 5234, "audit.kittyclaw.dev"));
                services.AddSingleton(new LocalizationService(new AppSettingsService(dir.Path, System.Globalization.CultureInfo.GetCultureInfo("en"))));
            }).Configure(app =>
            {
                app.Use(async (context, next) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Connection.LocalIpAddress = IPAddress.Loopback; context.Connection.LocalPort = 5234; await next(context); });
                app.UseMiddleware<RemoteTunnelMiddleware>(); app.UseMiddleware<NetworkAccessMiddleware>();
                app.Run(context => context.Response.WriteAsync("synthetic-protected-resource"));
            })).Start();
            Client = host.GetTestServer().CreateClient();
        }
        public void Dispose() { Client.Dispose(); host.Dispose(); dir.Dispose(); }
    }
}