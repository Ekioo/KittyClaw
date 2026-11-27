using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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

public sealed class AccountAssociationTests
{
    [Fact]
    public async Task Broker_authentication_requires_MFA_and_never_sends_account_secrets_to_other_origins()
    {
        using var dir = new TempDir();
        using var handler = new Broker();
        using var http = new HttpClient(handler);
        using var service = new AccountAssociationService(dir.Path, new EphemeralDataProtectionProvider(), http);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://remote-api.kittyclaw.dev/v1/instances");
        await Assert.ThrowsAsync<RemoteAccessException>(() => service.AddBrokerAuthenticationAsync(request, default));
        await service.BeginAssociationAsync(default);
        await service.RefreshAsync(default);
        await service.ConfirmAsync(handler.Account.Id, default);
        await Assert.ThrowsAsync<RemoteAccessException>(() => service.AddBrokerAuthenticationAsync(request, default));
        handler.MfaEnabled = true;
        await service.RefreshAsync(default);
        await service.AddBrokerAuthenticationAsync(request, default);
        Assert.Equal(service.InstallationId, request.Headers.GetValues("X-KittyClaw-Account-Installation").Single());
        Assert.Equal(handler.Credentials[0], request.Headers.GetValues("X-KittyClaw-Account-Credential").Single());
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "https://custom-broker.example/v1/instances");
        await Assert.ThrowsAsync<RemoteAccessException>(() => service.AddBrokerAuthenticationAsync(foreign, default));
        Assert.False(foreign.Headers.Contains("X-KittyClaw-Account-Credential"));
    }

    [Fact]
    public async Task Ownership_is_protected_persisted_and_requires_explicit_confirmation()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        using var handler = new Broker();
        using var http = new HttpClient(handler);
        using var service = new AccountAssociationService(dir.Path, protection, http);
        Assert.Null(service.Snapshot.Account);
        Assert.Empty(handler.Credentials);
        await service.BeginAssociationAsync(default);
        Assert.NotNull(service.Snapshot.AuthorizationUrl);
        var stored = await File.ReadAllTextAsync(Path.Combine(dir.Path, "account-association.json"));
        Assert.DoesNotContain(handler.Credentials[0], stored);
        using var restored = new AccountAssociationService(dir.Path, protection, http);
        await restored.RefreshAsync(default);
        Assert.Null(restored.Snapshot.Account);
        Assert.Equal(handler.Account, restored.Snapshot.ProposedAccount);
        await Assert.ThrowsAsync<AccountAssociationException>(() => restored.ConfirmAsync(Guid.NewGuid().ToString(), default));
        await restored.ConfirmAsync(handler.Account.Id, default);
        Assert.Equal(handler.Account, restored.Snapshot.Account);
        Assert.Null(restored.Snapshot.AuthorizationUrl);
        using var linked = new AccountAssociationService(dir.Path, protection, http);
        Assert.Equal(handler.Account, linked.Snapshot.Account);
        await linked.DisconnectAsync(default);
        Assert.Null(linked.Snapshot.Account);
        Assert.True(linked.Snapshot.Revoked);
        Assert.Single(handler.Credentials.Distinct());
    }

    [Fact]
    public async Task Untrusted_authorization_URL_and_revocation_are_handled_without_enabling_network_access()
    {
        using var dir = new TempDir();
        using var broker = new Broker { UnsafeUrl = true };
        using var http = new HttpClient(broker);
        using var service = new AccountAssociationService(dir.Path, new EphemeralDataProtectionProvider(), http);
        await Assert.ThrowsAsync<AccountAssociationException>(() => service.BeginAssociationAsync(default));
        Assert.Null(service.Snapshot.AuthorizationUrl);
        broker.UnsafeUrl = false;
        await service.BeginAssociationAsync(default);
        await service.RefreshAsync(default);
        await service.ConfirmAsync(broker.Account.Id, default);
        broker.Revoked = true;
        await service.RefreshAsync(default);
        Assert.True(service.Snapshot.Revoked);
        Assert.Null(service.Snapshot.Account);
        Assert.False(File.Exists(Path.Combine(dir.Path, "network-access.json")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "remote-access.json")));
    }

    [Fact]
    public async Task Local_forms_reject_remote_clients_rebinding_cross_origin_and_missing_csrf()
    {
        using var dir = new TempDir();
        using var handler = new Broker();
        using var http = new HttpClient(handler);
        using var service = new AccountAssociationService(dir.Path, new EphemeralDataProtectionProvider(), http);
        using var host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddAntiforgery();
            services.AddSingleton(service);
            services.AddSingleton(new LocalizationService(new AppSettingsService(dir.Path, CultureInfo.GetCultureInfo("en"))));
        }).Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = context.Request.Headers.ContainsKey("Test-Local") ? IPAddress.Loopback : IPAddress.Parse("192.168.1.5");
                await next(context);
            });
            app.UseMiddleware<AccountSetupMiddleware>();
            app.Run(context => context.Response.WriteAsync("next"));
        })).Start();
        using var client = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("http://localhost/account-setup")).StatusCode);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "http://evil.test/account-setup");
        rebound.Headers.Add("Test-Local", "true");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rebound)).StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "http://localhost/account-setup");
        crossOrigin.Headers.Add("Test-Local", "true"); crossOrigin.Headers.Add("Origin", "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossOrigin)).StatusCode);
        using var local = new HttpRequestMessage(HttpMethod.Get, "http://localhost/account-setup"); local.Headers.Add("Test-Local", "true");
        var page = await client.SendAsync(local);
        Assert.True(page.Headers.CacheControl?.NoStore);
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("target='_blank'", html);
        Assert.Contains("form-action 'self' https://my.kittyclaw.dev;", page.Headers.GetValues("Content-Security-Policy").Single());
        var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value);
        var cookie = string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        foreach (var valid in new[] { false, true })
        {
            using var post = new HttpRequestMessage(HttpMethod.Post, "http://localhost/account-setup");
            post.Headers.Add("Test-Local", "true"); post.Headers.Add("Origin", "http://localhost"); post.Headers.Add("Cookie", cookie);
            post.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["action"] = "start", ["__RequestVerificationToken"] = valid ? csrf : "invalid" });
            var started = await client.SendAsync(post);
            Assert.Equal(valid ? HttpStatusCode.Redirect : HttpStatusCode.BadRequest, started.StatusCode);
            if (valid)
            {
                Assert.StartsWith("https://my.kittyclaw.dev/associate?request=", started.Headers.Location!.AbsoluteUri);
                Assert.EndsWith("&local_return=http%3A%2F%2Flocalhost%2Faccount-setup", started.Headers.Location.AbsoluteUri);
            }
        }
        Assert.Single(handler.Credentials);
        using var returned = new HttpRequestMessage(HttpMethod.Get, "http://localhost/account-setup");
        returned.Headers.Add("Test-Local", "true");
        returned.Headers.Add("Sec-Fetch-Site", "cross-site");
        var confirmation = await client.SendAsync(returned);
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var confirmationHtml = await confirmation.Content.ReadAsStringAsync();
        Assert.Contains("name='action' value='confirm'", confirmationHtml);
        Assert.Null(service.Snapshot.Account);
        Assert.DoesNotContain("target='_blank'", confirmationHtml);
    }

    private sealed class Broker : HttpMessageHandler
    {
        public AssociatedAccount Account { get; } = new(Guid.NewGuid().ToString(), "Owner", "owner@example.test");
        public List<string> Credentials { get; } = [];
        public bool UnsafeUrl { get; set; }
        public bool Revoked { get; set; }
        public bool MfaEnabled { get; set; }
        private bool _confirmed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(AccountAssociationService.PortalOrigin, request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Null(request.Headers.Host);
            Assert.False(request.Headers.Contains("Cookie"));
            Credentials.Add(request.Headers.Authorization!.Parameter!);
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Matches("^[a-f0-9]{32}$", payload.GetProperty("instanceId").GetString()!);
            var action = request.RequestUri.Segments[^1];
            object response = new { ok = true };
            if (action == "associate") response = new { authorizationUrl = (UnsafeUrl ? "https://evil.test" : AccountAssociationService.PortalOrigin) + "/associate?request=" + new string('a', 64), expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600 };
            if (action == "status") response = new { account = _confirmed && !Revoked ? Account : null, proposedAccount = !_confirmed && !Revoked ? Account : null, revoked = Revoked, mfaEnabled = MfaEnabled };
            if (action == "confirm") _confirmed = true;
            if (action == "disconnect") { _confirmed = false; Revoked = true; }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
}
