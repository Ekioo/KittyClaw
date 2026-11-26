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
        var challenge = f.Portal.Begin(DateTimeOffset.UtcNow);
        var query = QueryHelpers.ParseQuery(new Uri(challenge.Url).Query);
        f.Handler.Challenge = query["challenge"].ToString();
        var cookie = await f.Portal.CompleteAsync(challenge.Cookie, query["state"].ToString(), FakeAccounts.Code, DateTimeOffset.UtcNow, default);
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
        foreach (var path in new[] { "/api/projects", "/mcp", "/openapi/v1.json", "/network-setup", "/account-setup" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/"); crossOrigin.Headers.Add("Origin", "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossOrigin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/access/logout", new FormUrlEncodedContent([]))).StatusCode);
        f.Handler.MfaEnabled = false; await f.Accounts.RefreshAsync(default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDir _directory = new();
        private readonly EphemeralDataProtectionProvider _protection = new();
        public FakeAccounts Handler { get; } = new();
        public AccountAssociationService Accounts { get; private set; } = null!;
        public AccountPortalAccessService Portal { get; private set; } = null!;
        public NetworkAccessService Network { get; private set; } = null!;
        public RemoteAccessStore Remote { get; private set; } = null!;
        public TaskCompletionSource StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HttpClient _http = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            f.Remote = new(f._directory.Path, f._protection);
            f.Remote.Prepare("https://remote-api.kittyclaw.dev", 5234, "office"); f.Remote.Enable();
            f._http = new(f.Handler);
            f.Accounts = new(f._directory.Path, f._protection, f._http, f.Remote);
            await f.Accounts.BeginAssociationAsync(default); await f.Accounts.RefreshAsync(default);
            await f.Accounts.ConfirmAsync(FakeAccounts.Account.Id, default); await f.Accounts.RefreshAsync(default);
            f.Portal = new(f.Accounts, f.Remote, f._protection);
            f.Network = new(f._directory.Path, f._protection, 5233);
            return f;
        }
        public async Task<WebApplication> StartServer(bool remote)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration["AllowedHosts"] = "office.kittyclaw.dev;localhost";
            builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(Accounts); builder.Services.AddSingleton(Portal); builder.Services.AddSingleton(Network);
            builder.Services.AddSingleton(Remote); builder.Services.AddSingleton(new RemoteTunnelOptions(true, 5234, "office.kittyclaw.dev"));
            builder.Services.AddSingleton(new LocalizationService(new AppSettingsService(_directory.Path)));
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider(); builder.Services.AddAntiforgery();
            var app = builder.Build();
            app.Use(async (context, next) => { context.Connection.LocalPort = remote ? 5234 : 5232; context.Connection.RemoteIpAddress = IPAddress.Loopback; await next(context); });
            app.UseMiddleware<RemoteTunnelMiddleware>(); app.UseMiddleware<AccountSetupMiddleware>();
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
        public bool MfaEnabled = true, Revoked, ProofValid = true;
        public string? Challenge;
        public int Redemptions;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var data = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            object result;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/installations/associate": result = new { authorizationUrl = AccountAssociationService.PortalOrigin + "/associate?request=" + new string('a', 64), expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() }; break;
                case "/v1/installations/status": result = new { account = _linked && !Revoked ? Account : null, proposedAccount = !_linked ? Account : null, revoked = Revoked, mfaEnabled = MfaEnabled }; break;
                case "/v1/installations/confirm": _linked = true; result = new { associated = true }; break;
                case "/v1/installations/access/redeem":
                    var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(data.GetProperty("verifier").GetString()!)));
                    if (data.GetProperty("code").GetString() != Code || challenge != Challenge || Redemptions++ > 0) return new(HttpStatusCode.Unauthorized);
                    result = new { proof = Proof, expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }; break;
                case "/v1/installations/access/check": result = new { valid = ProofValid && MfaEnabled && !Revoked, expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }; break;
                case "/v1/installations/access/revoke": ProofValid = false; result = new { revoked = true }; break;
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
