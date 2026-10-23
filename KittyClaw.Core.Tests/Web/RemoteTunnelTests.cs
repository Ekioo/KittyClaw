using System.Net;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Tests.Web;

public sealed class RemoteTunnelTests
{
    private const string Host = "kc-abcdef123456.kittyclaw.dev";

    [Theory]
    [InlineData("localhost")]
    [InlineData("kc-ab.kittyclaw.dev")]
    [InlineData("kc-abcdef.kittyclaw.dev.evil.test")]
    [InlineData("https://kc-abcdef.kittyclaw.dev")]
    public void Invalid_public_hosts_fail_configuration(string hostname)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteTunnel:Enabled"] = "true", ["RemoteTunnel:Hostname"] = hostname
        }).Build();
        Assert.Throws<InvalidOperationException>(() => RemoteTunnelOptions.Read(configuration));
    }

    [Fact]
    public void Listener_is_disabled_by_default() =>
        Assert.False(RemoteTunnelOptions.Read(new ConfigurationBuilder().Build()).Enabled);

    [Theory]
    [InlineData("localhost", "/", "127.0.0.1", 5234, 403)]
    [InlineData(Host, "/network-setup", "127.0.0.1", 5234, 403)]
    [InlineData(Host, "/", "192.168.1.42", 5234, 403)]
    [InlineData(Host, "/", "127.0.0.1", 5230, 403)]
    [InlineData(Host, "/api/projects", "127.0.0.1", 5234, 403)]
    [InlineData(Host, "/api/engine/health", "127.0.0.1", 5234, 401)]
    [InlineData(Host, "/api/projects/test/runs/abc123/stream", "127.0.0.1", 5234, 401)]
    [InlineData(Host, "/remote-setup", "127.0.0.1", 5234, 401)]
    [InlineData(Host, "/mcp", "127.0.0.1", 5234, 403)]
    [InlineData(Host, "/openapi/v1.json", "127.0.0.1", 5234, 403)]
    [InlineData(Host, "/uploads/private.png", "127.0.0.1", 5234, 401)]
    [InlineData(Host, "/_blazor/negotiate", "127.0.0.1", 5234, 401)]
    [InlineData(Host, "/", "::ffff:127.0.0.1", 5234, 401)]
    [InlineData(Host, "/access", "127.0.0.1", 5234, 200)]
    [InlineData("localhost", "/", "127.0.0.1", 5230, 200)]
    public async Task Dedicated_origin_cannot_inherit_local_trust(string host, string path, string peer, int port, int expected)
    {
        using var dir = new TempDir();
        var access = new NetworkAccessService(dir.Path, new EphemeralDataProtectionProvider(), 5233);
        var enrollment = access.CreateEnrollment();
        var secret = access.EnrollmentSecret(enrollment);
        var key = DecodeBase32(secret);
        Assert.True(access.Enable(enrollment, NetworkAccessService.Totp(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30)));
        using var testHost = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddAntiforgery();
            services.AddSingleton(access);
            services.AddSingleton(new RemoteTunnelOptions(true, 5234, Host));
            services.AddSingleton(new LocalizationService(new AppSettingsService(dir.Path,
                System.Globalization.CultureInfo.GetCultureInfo("en"))));
        }).Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                context.Connection.LocalIpAddress = IPAddress.Loopback;
                context.Connection.LocalPort = port;
                await next(context);
            });
            app.UseMiddleware<RemoteTunnelMiddleware>();
            app.UseMiddleware<NetworkAccessMiddleware>();
            app.Run(context => context.Response.WriteAsync("protected"));
        })).StartAsync();
        var server = testHost.GetTestServer();
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}{path}");
        request.Headers.Add("X-Forwarded-Host", "localhost");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        if (port == 5234 && host == Host && peer == "127.0.0.1" && path == "/access")
        {
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.DoesNotContain("protected", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Origin_is_closed_when_authentication_has_not_been_enrolled()
    {
        using var dir = new TempDir();
        var access = new NetworkAccessService(dir.Path, new EphemeralDataProtectionProvider(), 5233);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5234;
        context.Request.Host = new HostString(Host);
        var gate = new NetworkAccessMiddleware(_ => throw new Exception("Authentication was bypassed."));
        var tunnel = new RemoteTunnelMiddleware(c => gate.InvokeAsync(c, access, null!, null!), new(true, 5234, Host));
        await tunnel.InvokeAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; result.Add((byte)(buffer >> bits)); }
        }
        return result.ToArray();
    }
}
