using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Tests.Web;

public sealed class RemoteAccessConnectorTests
{
    private const string BrokerOrigin = "https://remote-api.kittyclaw.dev";

    [Fact]
    public void Identity_survives_restart_and_credentials_are_protected()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        var store = new RemoteAccessStore(dir.Path, protection);
        var identity = store.Prepare(BrokerOrigin, 5234, "My-Office");
        store.Enable();
        var protectedFile = File.ReadAllText(Path.Combine(dir.Path, "remote-access.json"));
        Assert.DoesNotContain(identity.Credential, protectedFile);
        Assert.DoesNotContain(identity.InstanceId, protectedFile);
        var reloaded = new RemoteAccessStore(dir.Path, protection);
        Assert.Equal(identity.InstanceId, reloaded.Identity!.InstanceId);
        Assert.Equal(identity.Credential, reloaded.Identity.Credential);
        Assert.True(reloaded.Snapshot.Enabled);
        Assert.Equal("https://my-office.kittyclaw.dev", reloaded.Snapshot.Url);
        reloaded.Pause();
        Assert.True(reloaded.Snapshot.Registered);
        Assert.Equal(store.Snapshot.Url, reloaded.Snapshot.Url);
        Assert.Throws<RemoteAccessException>(() => reloaded.Prepare("https://another.test", 5234));
        Assert.Throws<RemoteAccessException>(() => reloaded.Prepare(BrokerOrigin, 6000));
        File.WriteAllText(Path.Combine(dir.Path, "remote-access.json"), "corrupted");
        Assert.Throws<CryptographicException>(() => new RemoteAccessStore(dir.Path, protection));
    }

    [Fact]
    public async Task Lost_registration_response_reuses_identity_and_disabled_origin_requires_restart()
    {
        using var f = new Fixture();
        f.Handler.FailRegistration = true;
        await Assert.ThrowsAsync<RemoteAccessException>(() => f.Connector.ActivateAsync(default));
        var first = f.Store.Identity!;
        Assert.False(f.Store.Snapshot.Enabled);
        f.Handler.FailRegistration = false;
        await f.Connector.ActivateAsync(default);
        Assert.Equal(first.Credential, f.Store.Identity!.Credential);
        Assert.Equal(first.InstanceId, f.Store.Identity.InstanceId);
        Assert.True(f.Connector.NeedsRestart);
        Assert.Equal("RestartRequired", f.Connector.Status.State);
        Assert.Equal(0, f.Runtime.Starts);
    }

    [Fact]
    public async Task Full_capacity_has_a_specific_error_without_enabling_access_and_can_retry()
    {
        using var f = new Fixture();
        f.Handler.CapacityFull = true;
        var error = await Assert.ThrowsAsync<RemoteAccessException>(() => f.Connector.ActivateAsync(default));
        Assert.Equal("RemoteCapacityExhausted", error.Code);
        Assert.False(f.Store.Snapshot.Enabled);
        Assert.Equal(0, f.Runtime.Starts);
        var identity = f.Store.Identity!.InstanceId;
        f.Handler.CapacityFull = false;
        await f.Connector.ActivateAsync(default);
        Assert.Equal(identity, f.Store.Identity!.InstanceId);
        Assert.True(f.Store.Snapshot.Enabled);
    }

    [Fact]
    public async Task Activation_requires_authenticator_before_allocating_an_identity()
    {
        using var f = new Fixture(enrolled: false);
        Assert.Equal("RemoteAuthenticationRequired", (await Assert.ThrowsAsync<RemoteAccessException>(() =>
            f.Connector.ActivateAsync(default))).Code);
        Assert.Null(f.Store.Identity);
        Assert.Equal(0, f.Handler.Requests);
    }

    [Theory]
    [InlineData("http://remote-api.kittyclaw.dev")]
    [InlineData("https://user:secret@remote-api.kittyclaw.dev")]
    [InlineData("https://remote-api.kittyclaw.dev/path")]
    public void Broker_requires_a_clean_HTTPS_origin(string origin) =>
        Assert.Throws<InvalidOperationException>(() => new RemoteAccessBroker(new HttpClient(), new Uri(origin)));

    [Fact]
    public async Task Broker_rejects_a_response_for_another_origin_without_starting_runtime()
    {
        using var f = new Fixture();
        f.Handler.WrongHost = true;
        Assert.Equal("RemoteBrokerInvalid", (await Assert.ThrowsAsync<RemoteAccessException>(() =>
            f.Connector.ActivateAsync(default))).Code);
        Assert.False(f.Store.Snapshot.Enabled);
        Assert.Equal(0, f.Runtime.Starts);
    }

    [Fact]
    public async Task Broker_rejects_redirect_responses_without_returning_an_upstream_secret()
    {
        using var f = new Fixture();
        f.Handler.Redirect = true;
        var exception = await Assert.ThrowsAsync<RemoteAccessException>(() => f.Connector.ActivateAsync(default));
        Assert.Equal("RemoteBrokerUnavailable", exception.Code);
        Assert.DoesNotContain(f.Store.Identity!.Credential, exception.ToString());
        Assert.Equal(1, f.Handler.Requests);
    }

    [Fact]
    public async Task Supervisor_waits_for_readiness_cancels_on_pause_and_resumes_the_same_URL()
    {
        using var f = new Fixture(preconfigured: true);
        await f.Connector.StartAsync(default);
        try
        {
            await WaitUntil(() => f.Runtime.Starts == 1);
            Assert.NotEqual("Online", f.Connector.Status.State);
            f.Runtime.Readiness!(true);
            await WaitUntil(() => f.Connector.Status.State == "Online");
            var url = f.Store.Snapshot.Url;
            await f.Connector.PauseAsync(default);
            await WaitUntil(() => f.Runtime.Cancellations == 1);
            Assert.False(f.Store.Snapshot.Enabled);
            Assert.Equal("Disabled", f.Connector.Status.State);
            await f.Connector.ActivateAsync(default);
            await WaitUntil(() => f.Runtime.Starts == 2);
            Assert.Equal(url, f.Store.Snapshot.Url);
            Assert.Equal(0, f.Handler.Registrations);
            f.Access.Disable();
            await WaitUntil(() => f.Runtime.Cancellations == 2);
            await WaitUntil(() => f.Connector.Status.State == "AuthenticationRequired");
        }
        finally { await f.Connector.StopAsync(default); }
    }

    [Fact]
    public async Task Disabling_authentication_while_offline_prevents_resume_and_startup()
    {
        using var f = new Fixture(preconfigured: true);
        f.Access.Disable();
        await f.Connector.StartAsync(default);
        try
        {
            await WaitUntil(() => f.Connector.Status.State == "AuthenticationRequired");
            Assert.Equal(0, f.Runtime.Starts);
            await Assert.ThrowsAsync<RemoteAccessException>(() => f.Connector.ActivateAsync(default));
        }
        finally { await f.Connector.StopAsync(default); }
    }

    [Fact]
    public async Task Paused_origin_denies_a_reused_session_even_if_connector_keeps_forwarding()
    {
        using var f = new Fixture(preconfigured: true);
        var url = f.Store.Snapshot.Url!;
        await f.Connector.PauseAsync(default);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5234;
        context.Request.Host = new HostString(new Uri(url).Host);
        var gate = new RemoteTunnelMiddleware(_ => throw new Exception("Paused origin was reached"), f.Origin, f.Store);
        await gate.InvokeAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.True(f.Access.Enabled);
    }

    [Fact]
    public async Task Local_setup_requires_CSRF_and_consent_and_remote_setup_is_forbidden()
    {
        using var f = new Fixture();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddAntiforgery();
            services.AddSingleton(f.Store);
            services.AddSingleton(f.Connector);
            services.AddSingleton(f.Access);
            services.AddSingleton(new LocalizationService(new AppSettingsService(f.Directory, System.Globalization.CultureInfo.GetCultureInfo("en"))));
        }).Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                context.Connection.LocalIpAddress = IPAddress.Loopback;
                context.Connection.LocalPort = 5232;
                await next(context);
            });
            app.UseMiddleware<RemoteAccessSetupMiddleware>();
        })).StartAsync();
        using var client = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("https://kc-abcdef.kittyclaw.dev/remote-setup")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("http://localhost/remote-setup",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "activate" }))).StatusCode);
        var page = await client.GetAsync("http://localhost/remote-setup");
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("name='invitation'", html);
        var csrf = Regex.Match(html, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value;
        var cookie = page.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var post = new HttpRequestMessage(HttpMethod.Post, "http://localhost/remote-setup");
        post.Headers.Add("Cookie", cookie);
        post.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "activate", ["consent"] = "yes", ["__RequestVerificationToken"] = csrf
        });
        var result = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Found, result.StatusCode);
        var activated = await (await client.GetAsync("http://localhost/remote-setup")).Content.ReadAsStringAsync();
        Assert.Contains("Restart KittyClaw", activated);
        Assert.Contains(f.Store.Snapshot.Url!, activated);
        Assert.DoesNotContain(f.Store.Identity!.Credential, activated);
        Assert.DoesNotContain("name='invitation'", activated);
        f.Access.Disable();
        var withoutAuthenticator = await (await client.GetAsync("http://localhost/remote-setup")).Content.ReadAsStringAsync();
        Assert.Contains("Stop Internet access", withoutAuthenticator);
        using var pause = new HttpRequestMessage(HttpMethod.Post, "http://localhost/remote-setup");
        pause.Headers.Add("Cookie", cookie);
        pause.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "pause", ["__RequestVerificationToken"] = csrf
        });
        Assert.Equal(HttpStatusCode.Found, (await client.SendAsync(pause)).StatusCode);
        Assert.False(f.Store.Snapshot.Enabled);
    }

    [Fact]
    public async Task Connector_command_line_and_binary_validation_do_not_expose_credentials()
    {
        var info = CloudflaredRuntime.StartInfo("cloudflared", "sensitive-token", 12345);
        Assert.DoesNotContain("sensitive-token", string.Join(' ', info.ArgumentList));
        Assert.Equal("sensitive-token", info.Environment["TUNNEL_TOKEN"]);
        Assert.True(info.CreateNoWindow);
        Assert.Contains("127.0.0.1:12345", info.ArgumentList);
        using var dir = new TempDir();
        var binary = Path.Combine(dir.Path, "binary");
        await File.WriteAllTextAsync(binary, "tampered");
        await Assert.ThrowsAsync<RemoteAccessException>(() => CloudflaredRuntime.VerifyAsync(binary, new string('0', 64), default));
    }

    [Fact]
    public void Internal_UI_clients_use_a_trusted_loopback_listener()
    {
        Assert.Equal("http://localhost:25832/", LocalApiAddress.Resolve(
            ["http://localhost:25834", "https://*:25833", "http://127.0.0.1:25832"], 25834).ToString());
        Assert.Throws<InvalidOperationException>(() => LocalApiAddress.Resolve(["http://localhost:25834"], 25834));
    }

    [Fact]
    public async Task Image_upload_requires_exact_Origin_and_still_reaches_authentication_gate()
    {
        const string hostname = "kc-abcdef123456.kittyclaw.dev";
        foreach (var header in new[] { "", "https://evil.test", "https://" + hostname })
        {
            var reached = false;
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Connection.LocalPort = 5234;
            context.Request.Host = new HostString(hostname);
            context.Request.Method = "POST";
            context.Request.Path = "/api/images";
            context.Request.Headers.Origin = header;
            var middleware = new RemoteTunnelMiddleware(_ => { reached = true; return Task.CompletedTask; }, new(true, 5234, hostname));
            await middleware.InvokeAsync(context);
            Assert.Equal(header == "https://" + hostname, reached);
            if (!reached) Assert.Equal(403, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Retired_identity_waits_for_owner_intervention_instead_of_starting_a_connector()
    {
        using var f = new Fixture(preconfigured: true);
        f.Handler.Retired = true;
        await f.Connector.StartAsync(default);
        try
        {
            await WaitUntil(() => f.Connector.Status.State == "Error");
            Assert.Equal("RemoteRetired", f.Connector.Status.Error);
            Assert.Equal(0, f.Runtime.Starts);
            await f.Connector.PauseAsync(default);
            f.Handler.Retired = false;
            await f.Connector.ActivateAsync(default);
            await WaitUntil(() => f.Runtime.Starts == 1);
        }
        finally { await f.Connector.StopAsync(default); }
    }

    [Fact]
    public async Task Chosen_name_survives_restart_and_unavailable_names_can_retry_without_new_credentials()
    {
        using var f = new Fixture();
        f.Handler.NameTaken = true;
        Assert.Equal("RemoteNameUnavailable", (await Assert.ThrowsAsync<RemoteAccessException>(() =>
            f.Connector.ActivateAsync(default, "My-Office"))).Code);
        var first = f.Store.Identity!;
        Assert.True(first.NameRetryAllowed);
        f.Handler.NameTaken = false;
        await f.Connector.ActivateAsync(default, "Another-Office");
        Assert.Equal(first.Credential, f.Store.Identity!.Credential);
        Assert.Equal(first.InstanceId, f.Store.Identity.InstanceId);
        Assert.Equal("https://another-office.kittyclaw.dev", f.Store.Snapshot.Url);
        Assert.Equal("another-office.kittyclaw.dev", f.Store.OriginOptions(new()).Hostname);
        await f.Connector.PauseAsync(default);
        await f.Connector.ActivateAsync(default);
        Assert.Equal("https://another-office.kittyclaw.dev", f.Store.Snapshot.Url);
    }

    [Fact]
    public async Task Ambiguous_registration_keeps_its_original_name_for_retry()
    {
        using var f = new Fixture();
        f.Handler.FailRegistration = true;
        await Assert.ThrowsAsync<RemoteAccessException>(() => f.Connector.ActivateAsync(default, "my-office"));
        Assert.False(f.Store.Identity!.NameRetryAllowed);
        Assert.Equal("RemoteNameLocked", (await Assert.ThrowsAsync<RemoteAccessException>(() =>
            f.Connector.ActivateAsync(default, "another-office"))).Code);
        f.Handler.FailRegistration = false;
        await f.Connector.ActivateAsync(default);
        Assert.Equal("https://my-office.kittyclaw.dev", f.Store.Snapshot.Url);
    }

    [Theory]
    [InlineData("www")]
    [InlineData("remote-api")]
    [InlineData("kc-other")]
    [InlineData("a.b")]
    [InlineData("-office")]
    [InlineData("ab")]
    public async Task Invalid_names_are_refused_before_identity_allocation(string name)
    {
        using var f = new Fixture();
        Assert.Equal("RemoteNameInvalid", (await Assert.ThrowsAsync<RemoteAccessException>(() =>
            f.Connector.ActivateAsync(default, name))).Code);
        Assert.Null(f.Store.Identity);
        Assert.Equal(0, f.Handler.Requests);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class Runtime : ICloudflaredRuntime
    {
        public int Starts;
        public int Cancellations;
        public Action<bool>? Readiness;
        public async Task RunAsync(string token, Action<bool> readiness, CancellationToken ct)
        {
            Assert.Equal("private-tunnel-token", token);
            Readiness = readiness;
            Interlocked.Increment(ref Starts);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Interlocked.Increment(ref Cancellations); }
        }
    }
    private sealed class Handler(RemoteAccessStore store) : HttpMessageHandler
    {
        public bool FailRegistration;
        public bool CapacityFull;
        public bool WrongHost;
        public bool Redirect;
        public bool Retired;
        public bool NameTaken;
        public int Requests;
        public int Registrations;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Assert.Equal(store.Identity!.Credential, request.Headers.Authorization!.Parameter);
            if (Redirect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect));
            var connect = request.RequestUri!.AbsolutePath.EndsWith("/connect");
            if (connect && Retired) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
            if (!connect) Interlocked.Increment(ref Registrations);
            if (!connect && NameTaken) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                { Content = JsonContent.Create(new { error = "name_unavailable" }) });
            if (CapacityFull && !connect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = JsonContent.Create(new { error = "capacity_exhausted" }) });
            if (FailRegistration && !connect) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                instanceId = store.Identity.InstanceId,
                url = WrongHost ? "https://evil.test" : $"https://{RemoteAccessName.Hostname(store.Identity)}",
                state = connect ? "active" : "pending", originPort = store.Identity.Port,
                tunnelToken = connect ? "private-tunnel-token" : null
            }) });
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new();
        public string Directory => _dir.Path;
        public RemoteAccessStore Store { get; }
        public NetworkAccessService Access { get; }
        public Handler Handler { get; }
        public Runtime Runtime { get; } = new();
        public RemoteTunnelOptions Origin { get; }
        public RemoteAccessConnector Connector { get; }
        private readonly HttpClient _http;
        public Fixture(bool enrolled = true, bool preconfigured = false)
        {
            var protection = new EphemeralDataProtectionProvider();
            Store = new(_dir.Path, protection);
            Access = new(_dir.Path, protection, 5233);
            if (enrolled)
            {
                var enrollment = Access.CreateEnrollment();
                const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
                var bits = string.Concat(Access.EnrollmentSecret(enrollment).Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
                var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
                Assert.True(Access.Enable(enrollment, NetworkAccessService.Totp(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 - 1)));
            }
            if (preconfigured) { Store.Prepare(BrokerOrigin, 5234); Store.Enable(); }
            Origin = Store.OriginOptions(new());
            Handler = new(Store);
            _http = new HttpClient(Handler) { MaxResponseContentBufferSize = 64 * 1024 };
            Connector = new(Store, new(_http, new Uri(BrokerOrigin)), Runtime, Origin, Access, new Lifetime());
        }
        public void Dispose() { Connector.Dispose(); _http.Dispose(); _dir.Dispose(); }
    }
}
