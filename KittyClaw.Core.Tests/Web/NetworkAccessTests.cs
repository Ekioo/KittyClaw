using System.Net;
using System.Text;
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
using Microsoft.AspNetCore.Mvc.Testing;
using KittyClaw.Web.Api;

namespace KittyClaw.Core.Tests.Web;

public sealed class NetworkAccessTests
{
    [Fact]
    public async Task Six_digit_entry_submits_once_when_complete_and_supports_paste()
    {
        using var fixture = new Fixture(true);
        var response = await fixture.Client.GetAsync("https://pc.test:5233/access");
        var html = await response.Content.ReadAsStringAsync();
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 360, Height = 740 } });
        await page.RouteAsync("https://pc.test:5233/**", route => route.FulfillAsync(new()
        {
            Body = html, ContentType = "text/html",
            Headers = new Dictionary<string, string>
            {
                ["Content-Security-Policy"] = Assert.Single(response.Headers.GetValues("Content-Security-Policy"))
            }
        }));
        foreach (var paste in new[] { false, true })
        {
            await page.GotoAsync("https://pc.test:5233/access");
            await page.EvaluateAsync("""
                () => {
                    window.submissions = 0;
                    document.querySelector('form').addEventListener('submit', e => {
                        e.preventDefault(); window.submissions++;
                    });
                }
                """);
            Assert.Equal(6, await page.Locator(".enhanced .code-cells span").CountAsync());
            var input = page.Locator("input[name=code]");
            if (paste)
                await input.EvaluateAsync("""
                    input => {
                        const data = new DataTransfer(); data.setData('text', '012 345');
                        input.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
                    }
                    """);
            else
            {
                await input.PressSequentiallyAsync("01234");
                Assert.Equal(0, await page.EvaluateAsync<int>("window.submissions"));
                await input.PressSequentiallyAsync("5");
            }
            Assert.Equal("012345", await input.InputValueAsync());
            Assert.Equal("012345", string.Concat(await page.Locator(".code-cells span").AllTextContentsAsync()));
            Assert.Equal(1, await page.EvaluateAsync<int>("window.submissions"));
            await input.DispatchEventAsync("input");
            Assert.Equal(1, await page.EvaluateAsync<int>("window.submissions"));
        }
    }

    [Fact]
    public async Task Full_host_applies_saved_network_settings_and_keeps_local_UI()
    {
        using var dir = new TempDir();
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "network-keys")), options =>
        {
            if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi();
        });
        var access = new NetworkAccessService(dir.Path, protection, 5233);
        var (enrollment, key) = Enrollment(access);
        Assert.True(access.Enable(enrollment, Code(key, DateTimeOffset.UtcNow.AddSeconds(-30))));
        var previous = Environment.GetEnvironmentVariable("KITTYCLAW_DATA_DIR");
        var previousDesktop = Environment.GetEnvironmentVariable("KITTYCLAW_DESKTOP");
        try
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", dir.Path);
            Environment.SetEnvironmentVariable("KITTYCLAW_DESKTOP", "false");
            await using var factory = new StartupFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.True(factory.Services.GetRequiredService<NetworkAccessService>().Listening);
            var local = await client.GetAsync("http://localhost/network-setup");
            Assert.Equal(HttpStatusCode.OK, local.StatusCode);
            var setupHtml = await local.Content.ReadAsStringAsync();
            Assert.Contains("SHA-256", setupHtml);
            Assert.Contains("class='page-header setup-header'", setupHtml);
            Assert.Contains("href='/css/windows-desktop.css'", setupHtml);
            var setupPolicy = Assert.Single(local.Headers.GetValues("Content-Security-Policy"), policy => policy.Contains("default-src"));
            Assert.Contains("img-src data: 'self'", setupPolicy);
            Assert.Contains("style-src 'unsafe-inline' 'self'", setupPolicy);
            var settings = await client.GetAsync("http://localhost/settings");
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            var settingsHtml = await settings.Content.ReadAsStringAsync();
            Assert.Contains("network-global-settings", settingsHtml);
            // Middleware pages own their document, styles and scripts and require a full navigation.
            Assert.Contains("href=\"/network-setup\" data-enhance-nav=\"false\"", settingsHtml);
            Assert.Contains("href=\"/remote-setup\" data-enhance-nav=\"false\"", settingsHtml);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"https://{Environment.MachineName}/access")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"https://{Environment.MachineName}/api/projects")).StatusCode);
            // Phones use an interface IP and the explicit HTTPS port, not the machine name.
            foreach (var address in factory.Services.GetRequiredService<NetworkAccessService>().Addresses)
            {
                var origin = new UriBuilder("https", address.ToString(), 5233).Uri;
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(origin, "/access"))).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri(origin, "/api/projects"))).StatusCode);
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("https://evil.test/access")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITTYCLAW_DATA_DIR", previous);
            Environment.SetEnvironmentVariable("KITTYCLAW_DESKTOP", previousDesktop);
        }
    }

    private sealed class StartupFactory : WebApplicationFactory<CreateProjectRequest>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                // Exercise real startup and rendering without any automation or provider dispatch.
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(descriptor);
            });
        }
    }

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Totp_matches_RFC6238_SHA1_vectors(long seconds, string expected) =>
        Assert.Equal(expected, NetworkAccessService.Totp(Encoding.ASCII.GetBytes("12345678901234567890"), seconds / 30, 8));

    [Fact]
    public void Enrollment_replay_persistence_session_expiry_and_reset()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        var access = new NetworkAccessService(dir.Path, protection, 5233);
        Assert.False(access.Enabled);
        Assert.Null(access.Authenticate("123456", DateTimeOffset.UtcNow));
        var (enrollment, key) = Enrollment(access);
        var now = DateTimeOffset.UtcNow;
        var initial = Code(key, now);
        Assert.False(access.Enable(enrollment, "bad"));
        Assert.True(access.Enable(enrollment, initial));
        Assert.Null(access.Authenticate(initial, now));
        now = now.AddSeconds(60);
        var session = access.Authenticate(Code(key, now), now);
        Assert.NotNull(session);
        Assert.True(access.ValidSession(session, now));
        Assert.False(access.ValidSession(session + "tamper", now));
        Assert.False(access.ValidSession(session, now.AddHours(12)));
        var reloaded = new NetworkAccessService(dir.Path, protection, 5233);
        Assert.True(reloaded.Enabled);
        Assert.True(reloaded.ValidSession(session, now));
        Assert.Null(reloaded.Authenticate(Code(key, now), now));
        Assert.DoesNotContain(Convert.ToHexString(key), File.ReadAllText(Path.Combine(dir.Path, "network-access.json")));
        var revoked = false;
        reloaded.SessionsRevoked += () => revoked = true;
        reloaded.Disable();
        Assert.True(revoked);
        Assert.False(reloaded.ValidSession(session, now));
        Assert.False(new NetworkAccessService(dir.Path, protection, 5233).Enabled);
    }

    [Fact]
    public void Failed_attempts_are_throttled_even_with_a_valid_code()
    {
        using var dir = new TempDir();
        var access = new NetworkAccessService(dir.Path, new EphemeralDataProtectionProvider(), 5233);
        var (enrollment, key) = Enrollment(access);
        Assert.True(access.Enable(enrollment, Code(key, DateTimeOffset.UtcNow)));
        var now = DateTimeOffset.UtcNow.AddMinutes(2);
        for (var i = 0; i < 5; i++) Assert.Null(access.Authenticate("invalid", now));
        Assert.Null(access.Authenticate(Code(key, now), now));
        now = now.AddMinutes(1);
        Assert.NotNull(access.Authenticate(Code(key, now), now));
    }

    [Fact]
    public void Certificate_is_stable_and_private_key_is_protected_at_rest()
    {
        using var dir = new TempDir();
        var access = new NetworkAccessService(dir.Path, new EphemeralDataProtectionProvider(), 5233);
        using var first = access.Certificate();
        using var second = access.Certificate();
        Assert.True(first.HasPrivateKey);
        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.Contains("localhost", first.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.DnsName, false));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(dir.Path, "network-access.json.certificate")));
    }

    [Theory]
    [InlineData(null, "localhost", true)]
    [InlineData("127.0.0.1", "localhost", true)]
    [InlineData("::ffff:127.0.0.1", "localhost", true)]
    [InlineData("192.168.1.42", "localhost", false)]
    [InlineData("127.0.0.1", "evil.test", false)]
    public void Local_trust_requires_local_transport_and_host(string? peer, string host, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        Assert.Equal(expected, NetworkAccessMiddleware.IsLocal(context));
    }

    [Theory]
    [InlineData("/", false, 403)]
    [InlineData("/api/projects", false, 403)]
    [InlineData("/mcp", true, 401)]
    [InlineData("/_blazor/negotiate", true, 401)]
    [InlineData("/uploads/private.png", true, 401)]
    [InlineData("/network-setup", true, 403)]
    public async Task Every_remote_surface_is_gated(string path, bool enabled, int expected)
    {
        using var fixture = new Fixture(enabled);
        var response = await fixture.Client.GetAsync("https://pc.test" + path);
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task Local_setup_requires_real_loopback_host_and_antiforgery()
    {
        using var fixture = new Fixture(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/network-setup");
        request.Headers.Add("Test-Loopback", "true");
        var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("name='enrollment'", await response.Content.ReadAsStringAsync());
        using var post = new HttpRequestMessage(HttpMethod.Post, "http://localhost/network-setup");
        post.Headers.Add("Test-Loopback", "true");
        post.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "disable" });
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.SendAsync(post)).StatusCode);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "http://evil.test/network-setup");
        rebound.Headers.Add("Test-Loopback", "true");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.SendAsync(rebound)).StatusCode);
    }

    [Fact]
    public async Task Incorrect_login_code_keeps_the_form_and_allows_retry()
    {
        using var fixture = new Fixture(true);
        var login = await fixture.Client.GetAsync("https://pc.test:5233/access");
        Assert.Equal("same-origin", Assert.Single(login.Headers.GetValues("Referrer-Policy")));
        var html = await login.Content.ReadAsStringAsync();
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        // A seven-digit value is always rejected by TOTP validation.
        foreach (var code in new[] { "1234567", Code(fixture.Key!, DateTimeOffset.UtcNow) })
        {
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value);
            Assert.NotEmpty(csrf);
            using var post = new HttpRequestMessage(HttpMethod.Post, "https://pc.test:5233/access");
            post.Headers.Add("Origin", "https://pc.test:5233");
            post.Headers.Add("Cookie", cookies);
            post.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = csrf, ["code"] = code
            });
            var response = await fixture.Client.SendAsync(post);
            if (code.Length == 7)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                html = await response.Content.ReadAsStringAsync();
                Assert.Contains("role='alert'", html);
                Assert.Contains("Invalid, expired or already used code", html);
                Assert.Contains("<form", html);
            }
            else Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
    }

    [Fact]
    public async Task Login_sets_secure_cookie_gates_plain_http_and_rejects_cross_origin()
    {
        using var fixture = new Fixture(true);
        var login = await fixture.Client.GetAsync("https://pc.test/access");
        var html = await login.Content.ReadAsStringAsync();
        var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value);
        Assert.NotEmpty(csrf);
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        using var post = new HttpRequestMessage(HttpMethod.Post, "https://pc.test/access");
        post.Headers.Add("Cookie", cookies);
        post.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["code"] = Code(fixture.Key!, DateTimeOffset.UtcNow)
        });
        var response = await fixture.Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var sessionCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);
        var cookie = sessionCookie.Split(';')[0];
        using var allowed = new HttpRequestMessage(HttpMethod.Get, "https://pc.test/api/projects");
        allowed.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.SendAsync(allowed)).StatusCode);
        using var plain = new HttpRequestMessage(HttpMethod.Get, "http://pc.test/api/projects");
        plain.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.SendAsync(plain)).StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "https://pc.test/api/projects");
        crossOrigin.Headers.Add("Cookie", cookie);
        crossOrigin.Headers.Add("Origin", "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.SendAsync(crossOrigin)).StatusCode);
        fixture.Access.Disable();
        using var revoked = new HttpRequestMessage(HttpMethod.Get, "https://pc.test/api/projects");
        revoked.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.SendAsync(revoked)).StatusCode);
    }

    [Fact]
    public void Session_revocation_preserves_authenticator_and_invalidates_persisted_cookies()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        var access = new NetworkAccessService(dir.Path, protection, 5233);
        var (enrollment, key) = Enrollment(access);
        var now = DateTimeOffset.UtcNow;
        Assert.True(access.Enable(enrollment, Code(key, now.AddSeconds(-30))));
        var cookie = access.Authenticate(Code(key, now), now);
        Assert.NotNull(cookie);
        access.RevokeSessions();
        Assert.True(access.Enabled);
        Assert.False(access.ValidSession(cookie, now));
        var restored = new NetworkAccessService(dir.Path, protection, 5233);
        Assert.True(restored.Enabled);
        Assert.False(restored.ValidSession(cookie, now));
        Assert.NotNull(restored.Authenticate(Code(key, now.AddSeconds(30)), now.AddSeconds(30)));
    }

    private static (string Token, byte[] Key) Enrollment(NetworkAccessService access)
    {
        var token = access.CreateEnrollment();
        var text = access.EnrollmentSecret(token);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(text.Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        return (token, key);
    }
    private static string Code(byte[] key, DateTimeOffset now) => NetworkAccessService.Totp(key, now.ToUnixTimeSeconds() / 30);

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly TestServer _server;
        private readonly IHost _host;
        public NetworkAccessService Access { get; }
        public byte[]? Key { get; }
        public HttpClient Client { get; }
        public Fixture(bool enabled)
        {
            Access = new NetworkAccessService(_dir.Path, new EphemeralDataProtectionProvider(), 5233);
            if (enabled)
            {
                var (token, key) = Enrollment(Access);
                Key = key;
                // Enroll with the previous accepted time window so the current code can log in.
                Assert.True(Access.Enable(token, Code(key, DateTimeOffset.UtcNow.AddSeconds(-30))));
            }
            _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddAntiforgery();
                services.AddSingleton(Access);
                services.AddSingleton(new LocalizationService(new AppSettingsService(_dir.Path, System.Globalization.CultureInfo.GetCultureInfo("en"))));
            }).Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    context.Connection.RemoteIpAddress = context.Request.Headers.ContainsKey("Test-Loopback") ? IPAddress.Loopback : IPAddress.Parse("192.168.1.42");
                    await next(context);
                });
                app.UseMiddleware<NetworkAccessMiddleware>();
                app.Run(context => context.Response.WriteAsync("protected"));
            })).Start();
            _server = _host.GetTestServer();
            Client = _server.CreateClient();
        }
        public void Dispose() { Client.Dispose(); _host.Dispose(); _dir.Dispose(); }
    }
}
