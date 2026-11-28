using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Tests.Web;

public sealed class AccountPortalAccessTests
{
    [Fact]
    public async Task Agent_token_bypasses_only_approved_API_routes_and_is_rechecked_on_every_call()
    {
        await using var f = await Fixture.Create();
        await using var app = await f.StartServer(remote: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("https://office.kittyclaw.dev");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "kca_" + new string('e', 64));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/projects/alpha/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/projects/alpha/columns")).StatusCode);
        foreach (var path in new[] { "/api/projects/beta/tickets", "/api/projects/alpha/secrets", "/api/browse", "/mcp", "/settings", "/api/projects/alpha/runs/123/stream" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/projects/alpha/tickets", new { })).StatusCode);
        f.Handler.AgentWrite = true;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/projects/alpha/tickets", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/projects/alpha/tickets/1/transfer", new { targetProjectSlug = "beta" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/projects/alpha/chat/start", new { })).StatusCode);
        f.Handler.ProofValid = false;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects/alpha/tickets")).StatusCode);
        Assert.True(f.Handler.AgentChecks >= 10);
        f.Handler.ProofValid = true; f.Handler.AgentUnavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/projects/alpha/tickets")).StatusCode);
    }

    [Fact]
    public async Task Agent_pairing_returns_only_a_fixed_portal_link_and_validates_projects_and_bodies()
    {
        await using var f = await Fixture.Create();
        await f.Projects.CreateProjectAsync("Alpha"); await f.Projects.CreateProjectAsync("Beta");
        await using var app = await f.StartServer(remote: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("https://office.kittyclaw.dev");
        var start = await client.PostAsJsonAsync("/api/agent-access/start", new { name = "VM agent", projects = new[] { "alpha" }, write = true });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var response = await start.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://my.kittyclaw.dev/agents", response.GetProperty("verification_uri").GetString());
        Assert.Equal("no-store", start.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/agent-access/start", new { name = "VM", projects = new[] { "missing" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/agent-access/token", new { deviceCode = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/agent-access/start", new StringContent(new string('x', 5000), Encoding.UTF8, "application/json"))).StatusCode);
        var pending = await client.PostAsJsonAsync("/api/agent-access/token", new { deviceCode = new string('f', 64) });
        Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        Assert.Equal("authorization_pending", (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", "kca_" + new string('e', 64));
        var visible = await client.GetStringAsync("/api/projects");
        Assert.Contains("alpha", visible); Assert.DoesNotContain("beta", visible);
    }

    [Theory]
    [InlineData("Origin", "https://office.kittyclaw.dev")]
    [InlineData("Origin", "https://evil.test")]
    [InlineData("Cookie", "anything=1")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    public async Task Agent_tokens_and_pairing_reject_browser_credentials(string header, string value)
    {
        await using var f = await Fixture.Create();
        await using var app = await f.StartServer(remote: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("https://office.kittyclaw.dev");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "kca_" + new string('e', 64));
        client.DefaultRequestHeaders.Add(header, value);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/projects/alpha/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/agent-access/start", new { name = "VM", projects = new[] { "alpha" } })).StatusCode);
        Assert.Equal(0, f.Handler.AgentChecks);
    }

    [Fact]
    public async Task One_account_factor_can_resume_remote_access_without_a_local_authenticator()
    {
        await using var f = await Fixture.Create();
        using var http = new HttpClient(f.Handler, disposeHandler: false);
        using var connector = new RemoteAccessConnector(f.Remote,
            new RemoteAccessBroker(http, new Uri("https://remote-api.kittyclaw.dev")), new UnusedRuntime(),
            new(true, 5234, "office.kittyclaw.dev"), f.Network, new Lifetime(), f.Accounts);
        f.Remote.Pause();
        await connector.ActivateAsync(default);
        Assert.True(f.Remote.Snapshot.Enabled);
        Assert.False(f.Network.Enabled);
        f.Remote.SetConnected(true);
        await connector.PauseAsync(default);
        Assert.False(f.Remote.Snapshot.Connected);
        f.Handler.MfaEnabled = false;
        var denied = await Assert.ThrowsAsync<RemoteAccessException>(() => connector.ActivateAsync(default));
        Assert.Equal("RemoteAuthenticationRequired", denied.Code);
        Assert.False(f.Remote.Snapshot.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_remote_stream_is_aborted_when_the_account_session_or_factor_is_revoked(bool localRefresh)
    {
        await using var f = await Fixture.Create();
        var challenge = await f.Portal.BeginAsync("tunnel", "https://office.kittyclaw.dev", DateTimeOffset.UtcNow, default);
        var query = QueryHelpers.ParseQuery(new Uri(challenge.Url).Query);
        f.Handler.Challenge = query["challenge"].ToString();
        var cookie = await f.Portal.CompleteAsync(challenge.Cookie, query["state"].ToString(), FakeAccounts.Code, "tunnel", "https://office.kittyclaw.dev", DateTimeOffset.UtcNow, default);
        await using var app = await f.StartServer(remote: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("https://office.kittyclaw.dev");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-KittyClawPortalSession=" + cookie);
        var request = client.GetStringAsync("/long");
        await f.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Handler.ProofValid = false;
        if (localRefresh) { f.Handler.MfaEnabled = false; await f.Accounts.RefreshAsync(default); }
        await Assert.ThrowsAnyAsync<Exception>(async () => await request).WaitAsync(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task Stream_revalidation_bypasses_positive_cache_but_preserves_local_revocation()
    {
        await using var f = await Fixture.Create();
        var challenge = f.Portal.Begin(DateTimeOffset.UtcNow);
        var query = QueryHelpers.ParseQuery(new Uri(challenge.Url).Query);
        f.Handler.Challenge = query["challenge"].ToString();
        var cookie = await f.Portal.CompleteAsync(challenge.Cookie, query["state"].ToString(), FakeAccounts.Code, DateTimeOffset.UtcNow, default);
        Assert.True(await f.Portal.ValidAsync(cookie, default));
        f.Handler.ProofValid = false;
        Assert.True(await f.Portal.ValidAsync(cookie, default));
        Assert.False(await f.Portal.ValidAsync(cookie, "tunnel", null, default, revalidate: true));

        // A failed central sign-out must not let monitoring resurrect a locally revoked proof.
        f.Handler.ProofValid = true;
        f.Handler.RejectRevocation = true;
        await f.Portal.RevokeAsync(cookie, default);
        Assert.False(await f.Portal.ValidAsync(cookie, "tunnel", null, default, revalidate: true));
    }

    [Fact]
    public async Task Challenge_is_browser_bound_and_code_is_redeemed_only_with_its_protected_verifier()
    {
        await using var f = await Fixture.Create();
        var now = DateTimeOffset.UtcNow;
        var challenge = f.Portal.Begin(now);
        var state = QueryHelpers.ParseQuery(new Uri(challenge.Url).Query)["state"].ToString();
        f.Handler.Challenge = QueryHelpers.ParseQuery(new Uri(challenge.Url).Query)["challenge"].ToString();
        await Assert.ThrowsAsync<AccountAssociationException>(() => f.Portal.CompleteAsync(null, state, FakeAccounts.Code, now, default));
        await Assert.ThrowsAsync<AccountAssociationException>(() => f.Portal.CompleteAsync(challenge.Cookie, new string('f', 64), FakeAccounts.Code, now, default));
        await Assert.ThrowsAsync<AccountAssociationException>(() => f.Portal.CompleteAsync(challenge.Cookie, state, FakeAccounts.Code, now.AddMinutes(6), default));
        Assert.Equal(0, f.Handler.Redemptions);
        var cookie = await f.Portal.CompleteAsync(challenge.Cookie, state, FakeAccounts.Code, now, default);
        Assert.DoesNotContain(FakeAccounts.Proof, cookie);
        Assert.True(await f.Portal.ValidAsync(cookie, default));
        Assert.Null(f.Portal.ReadSession(cookie, now.AddHours(13)));
        var changed = cookie[..20] + (cookie[20] == 'A' ? 'B' : 'A') + cookie[21..];
        Assert.False(await f.Portal.ValidAsync(changed, default));
        await Assert.ThrowsAsync<AccountAssociationException>(() => f.Portal.CompleteAsync(challenge.Cookie, state, FakeAccounts.Code, now, default));
        await f.Portal.RevokeAsync(cookie, default);
        Assert.False(await f.Portal.ValidAsync(cookie, default));
        Assert.False(f.Handler.ProofValid);
    }

    [Fact]
    public async Task Account_revocation_and_MFA_disable_invalidate_cached_access_without_enabling_LAN()
    {
        await using var f = await Fixture.Create();
        Assert.False(f.Network.Enabled);
        var challenge = f.Portal.Begin(DateTimeOffset.UtcNow);
        var query = QueryHelpers.ParseQuery(new Uri(challenge.Url).Query);
        f.Handler.Challenge = query["challenge"].ToString();
        var cookie = await f.Portal.CompleteAsync(challenge.Cookie, query["state"].ToString(), FakeAccounts.Code, DateTimeOffset.UtcNow, default);
        Assert.True(await f.Portal.ValidAsync(cookie, default));
        f.Handler.MfaEnabled = false;
        await f.Accounts.RefreshAsync(default);
        Assert.False(await f.Portal.ValidAsync(cookie, default));
        Assert.False(f.Network.Enabled);
        f.Handler.MfaEnabled = true; f.Handler.ProofValid = false;
        await f.Accounts.RefreshAsync(default);
        Assert.False(await f.Portal.ValidAsync(cookie, default));
        f.Handler.Revoked = true;
        await f.Accounts.RefreshAsync(default);
        Assert.Null(f.Accounts.Snapshot.Account);
        Assert.Throws<AccountAssociationException>(() => f.Portal.Begin(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Tunnel_SSO_keeps_origin_API_setup_and_cross_origin_boundaries()
    {
        await using var f = await Fixture.Create();
        await using var app = await f.StartServer(remote: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("https://office.kittyclaw.dev");
        foreach (var setup in new[] { "/access-setup", "/account-setup" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(setup)).StatusCode);
        var start = await client.GetAsync("/account-access");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        f.Handler.Challenge = query["challenge"].ToString();
        var challengeCookie = start.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", challengeCookie.ToLowerInvariant());
        Assert.Contains("httponly", challengeCookie.ToLowerInvariant());
        client.DefaultRequestHeaders.Add("Cookie", challengeCookie.Split(';')[0]);
        var callback = await client.GetAsync($"/account-access/callback?state={query["state"]}&code={FakeAccounts.Code}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        var sessionCookie = callback.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-KittyClawPortalSession="));
        Assert.DoesNotContain("domain=", sessionCookie.ToLowerInvariant());
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", sessionCookie.Split(';')[0]);
        Assert.Equal("protected", await client.GetStringAsync("/"));
        Assert.False(f.Network.Enabled);
        foreach (var path in new[] { "/api/projects", "/mcp", "/openapi/v1.json", "/network-setup" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        await AssertRemoteSummaries(client);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/"); crossOrigin.Headers.Add("Origin", "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossOrigin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/access/logout", new FormUrlEncodedContent([]))).StatusCode);
        f.Handler.MfaEnabled = false; await f.Accounts.RefreshAsync(default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
        foreach (var setup in new[] { "/access-setup", "/account-setup" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(setup)).StatusCode);
    }

    [Fact]
    public async Task Local_listener_cannot_issue_portal_cookies_even_with_forwarded_headers()
    {
        await using var f = await Fixture.Create();
        await using var app = await f.StartServer(remote: false);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("http://localhost");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "office.kittyclaw.dev");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var response = await client.GetAsync("/account-access");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Network_setup_uses_account_MFA_without_another_QR_and_requires_CSRF_for_opt_in()
    {
        await using var f = await Fixture.Create();
        f.Remote.Pause();
        await using var app = await f.StartServer(remote: false);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("http://localhost");
        var page = await client.GetAsync("/access-setup");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("my.kittyclaw.dev/security", html);
        Assert.Contains("name='mode' value='network'", html);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "type='radio' name='mode'").Count);
        Assert.Contains("data-current-mode='local'", html);
        Assert.Contains("<noscript><button>", html);
        Assert.Contains(AccessSetupView.Script, html);
        var nonce = System.Text.RegularExpressions.Regex.Match(html, "<script nonce='([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains("script-src 'nonce-" + nonce + "'", Assert.Single(page.Headers.GetValues("Content-Security-Policy"), value => value.Contains("default-src")));
        Assert.DoesNotContain("otpauth:", html); Assert.DoesNotContain("name='code'", html); Assert.DoesNotContain("name='enrollment'", html);
        Assert.False(f.Network.Enabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/access-setup", new FormUrlEncodedContent(new Dictionary<string,string> { ["action"] = "mode", ["mode"] = "network" }))).StatusCode);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value;
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var enabled = await client.PostAsync("/access-setup", new FormUrlEncodedContent(new Dictionary<string,string> { ["action"] = "mode", ["mode"] = "network", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, enabled.StatusCode);
        Assert.True(f.Network.AccountAccessEnabled);
        f.Handler.MfaEnabled = false;
        var locked = await (await client.GetAsync("/access-setup")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("otpauth:", locked); Assert.DoesNotContain("name='action' value='enable'", locked);
    }

    [Fact]
    public async Task Unified_access_form_replaces_old_pages_and_refuses_unconsented_Internet_or_invalid_modes()
    {
        await using var f = await Fixture.Create(); f.Remote.Pause();
        await using var app = await f.StartServer(remote: false);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("http://localhost");
        foreach (var old in new[] { "/network-setup", "/remote-setup" })
        {
            var redirect = await client.GetAsync(old); Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.Equal("/access-setup", redirect.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.Gone, (await client.PostAsync(old, new FormUrlEncodedContent([]))).StatusCode);
        }
        var page = await client.GetAsync("/access-setup"); var html = await page.Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(html, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value;
        client.DefaultRequestHeaders.Add("Cookie", page.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        foreach (var mode in new[] { "internet", "invalid", "Network,Internet" })
        {
            var rejected = await client.PostAsync("/access-setup", new FormUrlEncodedContent(new Dictionary<string,string> { ["action"]="mode", ["mode"]=mode, ["__RequestVerificationToken"]=token }));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.False(f.Remote.Snapshot.Enabled); Assert.False(f.Network.Enabled);
        }
        var local = await client.PostAsync("/access-setup", new FormUrlEncodedContent(new Dictionary<string,string> { ["action"]="mode", ["mode"]="local", ["subdomain"]="invalid input", ["return"]="https://evil.test", ["__RequestVerificationToken"]=token }));
        Assert.Equal(HttpStatusCode.Redirect, local.StatusCode); Assert.Equal("/access-setup", local.Headers.Location!.ToString());
        using var cross = new HttpRequestMessage(HttpMethod.Post, "/access-setup"); cross.Headers.Add("Origin","https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(cross)).StatusCode);
    }

    [Fact]
    public async Task LAN_sign_in_requires_the_same_account_MFA_and_cannot_reuse_a_tunnel_or_foreign_host_cookie()
    {
        await using var f = await Fixture.Create();
        f.Remote.Pause();
        f.Network.EnableAccountAccess(); f.Network.Listening = true;
        var origin = $"https://{Environment.MachineName.ToLowerInvariant()}:5233";
        await using var app = await f.StartServer(remote: false, network: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri(origin);
        var start = await client.GetAsync("/account-access");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Contains("/authorize?request=", start.Headers.Location!.ToString());
        var challengeCookie = start.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", challengeCookie); Assert.Contains("httponly", challengeCookie);
        client.DefaultRequestHeaders.Add("Cookie", challengeCookie.Split(';')[0]);
        var callback = await client.GetAsync($"/account-access/callback?state={f.Handler.State}&code={FakeAccounts.Code}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var sessionCookie = callback.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-KittyClawPortalSession="));
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", sessionCookie.Split(';')[0]);
        Assert.Equal("protected", await client.GetStringAsync("/"));
        Assert.Equal("protected", await client.GetStringAsync("/api/projects"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/network-setup")).StatusCode);
        await AssertRemoteSummaries(client);
        Assert.False(await f.Portal.ValidAsync(sessionCookie.Split(';')[0].Split('=',2)[1], "tunnel", "https://office.kittyclaw.dev", default));
        Assert.False(await f.Portal.ValidAsync(sessionCookie.Split(';')[0].Split('=',2)[1], "network", "https://192.168.1.21:5233", default));
        using var cross = new HttpRequestMessage(HttpMethod.Get, "/"); cross.Headers.Add("Origin", "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(cross)).StatusCode);
        f.Network.SetNetworkRules(true, "192.168.1.80");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
        f.Network.SetNetworkRules(false, "");
        f.Network.Disable(); f.Network.EnableAccountAccess();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("factor")]
    [InlineData("disable")]
    [InlineData("rules")]
    public async Task Existing_LAN_stream_is_aborted_when_account_or_network_access_is_revoked(string reason)
    {
        await using var f = await Fixture.Create(); f.Network.EnableAccountAccess(); f.Network.Listening = true;
        var origin = $"https://{Environment.MachineName.ToLowerInvariant()}:5233";
        var challenge = await f.Portal.BeginAsync("network", origin, DateTimeOffset.UtcNow, default);
        var cookie = await f.Portal.CompleteAsync(challenge.Cookie, f.Handler.State!, FakeAccounts.Code, "network", origin, DateTimeOffset.UtcNow, default);
        await using var app = await f.StartServer(remote: false, network: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri(origin);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-KittyClawPortalSession=" + cookie);
        var request = client.GetStringAsync("/long");
        await f.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reason == "factor") { f.Handler.MfaEnabled = false; await f.Accounts.RefreshAsync(default); }
        else if (reason == "disable") f.Network.Disable();
        else f.Network.SetNetworkRules(true, "192.168.1.80");
        await Assert.ThrowsAnyAsync<Exception>(async () => await request).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Legacy_local_authenticator_cannot_bypass_the_account_gate()
    {
        await using var f = await Fixture.Create();
        var enrollment = f.Network.CreateEnrollment();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(f.Network.EnrollmentSecret(enrollment).Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        Assert.True(f.Network.Enable(enrollment, NetworkAccessService.Totp(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 - 1)));
        var legacy = f.Network.Authenticate(NetworkAccessService.Totp(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), DateTimeOffset.UtcNow);
        Assert.NotNull(legacy);
        await using var app = await f.StartServer(remote: true);
        using var client = app.GetTestClient(); client.BaseAddress = new Uri("https://office.kittyclaw.dev");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-KittyClawAccess=" + legacy);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/access", new FormUrlEncodedContent(new Dictionary<string,string> { ["code"] = "123456" }))).StatusCode);
        f.Network.EnableAccountAccess(); Assert.Null(f.Network.Authenticate("123456", DateTimeOffset.UtcNow));
    }

    private static async Task AssertRemoteSummaries(HttpClient client)
    {
        foreach (var path in new[] { "/account-setup", "/access-setup" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("href='/settings'", html);
            Assert.Contains("my.kittyclaw.dev/security", html);
            Assert.DoesNotContain("<form", html); Assert.DoesNotContain("otpauth:", html);
            Assert.DoesNotContain(FakeAccounts.Proof, html); Assert.DoesNotContain(FakeAccounts.Code, html);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            using var cross = new HttpRequestMessage(HttpMethod.Get, path); cross.Headers.Add("Origin", "https://evil.test");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(cross)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, new FormUrlEncodedContent(
                new Dictionary<string,string> { ["action"] = path == "/account-setup" ? "disconnect" : "mode", ["mode"] = "local" }))).StatusCode);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDir _directory = new();
        private readonly EphemeralDataProtectionProvider _protection = new();
        public FakeAccounts Handler { get; } = new();
        public AccountAssociationService Accounts { get; private set; } = null!;
        public AccountPortalAccessService Portal { get; private set; } = null!;
        public NetworkAccessService Network { get; private set; } = null!;
        public RemoteAccessStore Remote { get; private set; } = null!;
        public ProjectService Projects { get; private set; } = null!;
        public TaskCompletionSource StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HttpClient _http = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            f.Projects = new(f._directory.Path);
            f.Remote = new(f._directory.Path, f._protection);
            f.Remote.Prepare("https://remote-api.kittyclaw.dev", 5234, "office"); f.Remote.Enable();
            f._http = new(f.Handler);
            f.Accounts = new(f._directory.Path, f._protection, f._http, f.Remote);
            await f.Accounts.BeginAssociationAsync(default); await f.Accounts.RefreshAsync(default);
            await f.Accounts.ConfirmAsync(FakeAccounts.Account.Id, default); await f.Accounts.RefreshAsync(default);
            f.Network = new(f._directory.Path, f._protection, 5233);
            f.Portal = new(f.Accounts, f.Remote, f._protection, f.Network);
            return f;
        }
        public async Task<WebApplication> StartServer(bool remote, bool network = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration["AllowedHosts"] = $"office.kittyclaw.dev;localhost;{Environment.MachineName}";
            builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(Accounts); builder.Services.AddSingleton(Portal); builder.Services.AddSingleton(Network);
            builder.Services.AddSingleton(Projects); builder.Services.AddSingleton<AgentApiAccessService>();
            builder.Services.AddSingleton(Remote); builder.Services.AddSingleton(new RemoteTunnelOptions(true, 5234, "office.kittyclaw.dev"));
            builder.Services.AddSingleton(new RemoteAccessConnector(Remote, new(_http, new Uri("https://remote-api.kittyclaw.dev")),
                new UnusedRuntime(), new(true,5234,"office.kittyclaw.dev"), Network, new Lifetime(), Accounts));
            builder.Services.AddSingleton<AccessModeService>();
            builder.Services.AddSingleton(new LocalizationService(new AppSettingsService(_directory.Path)));
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider(); builder.Services.AddAntiforgery();
            var app = builder.Build();
            app.Use(async (context, next) => { context.Connection.LocalPort = remote ? 5234 : network ? 5233 : 5232; context.Connection.RemoteIpAddress = network ? IPAddress.Parse("192.168.1.70") : IPAddress.Loopback; await next(context); });
            app.UseMiddleware<RemoteTunnelMiddleware>(); app.UseRemoteSetupSummaries(); app.UseMiddleware<AccountSetupMiddleware>();
            app.UseMiddleware<AccessSetupMiddleware>();
            app.UseMiddleware<AccountPortalAccessMiddleware>(); app.UseMiddleware<NetworkAccessMiddleware>();
            app.Run(async context =>
            {
                if (context.Request.Path == "/long")
                {
                    StreamStarted.TrySetResult();
                    await context.Response.WriteAsync("stream");
                    await context.Response.Body.FlushAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                else await context.Response.WriteAsync("protected");
            });
            await app.StartAsync(); return app;
        }
        public ValueTask DisposeAsync() { Accounts.Dispose(); _http.Dispose(); Handler.Dispose(); _directory.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class FakeAccounts : HttpMessageHandler
    {
        public static readonly AssociatedAccount Account = new("f8e66066-4b2e-41d4-8e48-96e3204ee113", "Owner", "owner@example.test");
        public static readonly string Code = new('c', 64), Proof = new('d', 64);
        private bool _linked;
        public bool MfaEnabled = true, Revoked, ProofValid = true, RejectRevocation;
        public string? Challenge;
        public string? State;
        private string? _proofKind, _proofOrigin;
        public int Redemptions;
        public bool AgentWrite, AgentUnavailable;
        public int AgentChecks;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var data = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            object result;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/installations/agents/start":
                    result = new { verification_uri = "https://my.kittyclaw.dev/agents", user_code = "ABCD-EFGH-JKLM", device_code = new string('f', 64), expires_in = 600, interval = 5 }; break;
                case "/v1/installations/agents/token": result = new { error = "authorization_pending" }; break;
                case "/v1/installations/agents/check":
                    AgentChecks++;
                    if (AgentUnavailable) return new(HttpStatusCode.ServiceUnavailable);
                    result = new { valid = ProofValid, projects = new[] { "alpha" }, write = AgentWrite, expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), id = "agent-id" }; break;
                case "/v1/installations/associate": result = new { authorizationUrl = AccountAssociationService.PortalOrigin + "/associate?request=" + new string('a', 64), expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() }; break;
                case "/v1/installations/status": result = new { account = _linked && !Revoked ? Account : null, proposedAccount = !_linked ? Account : null, revoked = Revoked, mfaEnabled = MfaEnabled }; break;
                case "/v1/installations/confirm": _linked = true; result = new { associated = true }; break;
                case "/v1/installations/access/start":
                    Challenge = data.GetProperty("challenge").GetString(); State = data.GetProperty("state").GetString();
                    result = new { authorizationUrl = AccountAssociationService.PortalOrigin + "/instances/" + data.GetProperty("instanceId").GetString() + "/authorize?request=" + new string('a', 64) }; break;
                case "/v1/installations/access/redeem":
                    var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(data.GetProperty("verifier").GetString()!)));
                    if (data.GetProperty("code").GetString() != Code || challenge != Challenge || Redemptions++ > 0) return new(HttpStatusCode.Unauthorized);
                    _proofKind = data.GetProperty("kind").GetString(); _proofOrigin = data.GetProperty("origin").GetString();
                    result = new { proof = Proof, expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }; break;
                case "/v1/installations/access/check": result = new { valid = ProofValid && MfaEnabled && !Revoked && _proofKind == data.GetProperty("kind").GetString() && _proofOrigin == data.GetProperty("origin").GetString(), expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }; break;
                case "/v1/installations/access/revoke":
                    if (RejectRevocation) return new(HttpStatusCode.ServiceUnavailable);
                    ProofValid = false; result = new { revoked = true }; break;
                default: throw new InvalidOperationException("Unexpected account request");
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
    private sealed class UnusedRuntime : ICloudflaredRuntime
    {
        public Task RunAsync(string token, Action<bool> ready, CancellationToken ct) => throw new InvalidOperationException("No live tunnel should start in this test");
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
