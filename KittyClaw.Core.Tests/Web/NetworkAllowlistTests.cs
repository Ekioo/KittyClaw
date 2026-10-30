using System.Net;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace KittyClaw.Core.Tests.Web;

public sealed class NetworkAllowlistTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Updating_rules_aborts_denied_direct_transports_but_preserves_tunnel(bool tunnel)
    {
        using TempDir directory = new();
        var access = new NetworkAccessService(directory.Path, new EphemeralDataProtectionProvider(), 5233);
        var enrollment = access.CreateEnrollment();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(access.EnrollmentSecret(enrollment).Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var step = now.ToUnixTimeSeconds() / 30;
        Assert.True(access.Enable(enrollment, NetworkAccessService.Totp(key, step)));
        var session = access.Authenticate(NetworkAccessService.Totp(key, step + 1), now);
        Assert.NotNull(session);
        access.SetNetworkRules(true, tunnel ? "" : "192.168.1.42");
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.42");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("pc.test");
        context.Request.Headers.Cookie = "__Host-KittyClawAccess=" + session;
        using var aborted = new CancellationTokenSource();
        context.RequestAborted = aborted.Token;
        // DefaultHttpContext has no server lifetime feature; install an abortable transport.
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(new Lifetime(aborted));
        if (tunnel) context.Features.Set(new RemoteTunnelRequest());
        var reached = false;
        var middleware = new NetworkAccessMiddleware(_ =>
        {
            reached = true;
            access.SetNetworkRules(true, "");
            Assert.Equal(!tunnel, aborted.IsCancellationRequested);
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context, access, null!, null!);
        Assert.True(reached);
    }

    private sealed class Lifetime(CancellationTokenSource source) : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } = source.Token;
        public void Abort() => source.Cancel();
    }

    [Theory]
    [InlineData("192.168.1.42", true)]
    [InlineData("::ffff:192.168.1.42", true)]
    [InlineData("192.168.1.43", false)]
    [InlineData("10.2.3.255", true)]
    [InlineData("10.2.4.0", false)]
    [InlineData("fd00::1234", true)]
    [InlineData("fd01::1", false)]
    public void Matches_individual_addresses_and_network_boundaries(string peer, bool expected)
    {
        using TempDir directory = new();
        var access = new NetworkAccessService(directory.Path, new EphemeralDataProtectionProvider(), 5233);
        access.SetNetworkRules(true, "192.168.1.42\n10.2.3.0/24\nfd00::/64");
        Assert.Equal(expected, access.AllowsNetwork(IPAddress.Parse(peer)));
    }

    [Fact]
    public void Rules_persist_and_invalid_updates_leave_previous_rules_intact()
    {
        using TempDir directory = new();
        var protection = new EphemeralDataProtectionProvider();
        var access = new NetworkAccessService(directory.Path, protection, 5233);
        Assert.True(access.AllowsNetwork(IPAddress.Parse("192.168.1.42")));
        access.SetNetworkRules(true, "192.168.1.42");
        Assert.Throws<ArgumentException>(() => access.SetNetworkRules(false, "192.168.1.42\ninvalid"));
        access.Disable();
        var restored = new NetworkAccessService(directory.Path, protection, 5233);
        Assert.True(restored.RestrictNetwork);
        Assert.True(restored.AllowsNetwork(IPAddress.Parse("192.168.1.42")));
        Assert.False(restored.AllowsNetwork(IPAddress.Parse("192.168.1.43")));
        restored.SetNetworkRules(true, "");
        Assert.False(restored.AllowsNetwork(IPAddress.Parse("192.168.1.42")));
        Assert.False(restored.AllowsNetwork(null));
    }

    [Theory]
    [InlineData("/access")]
    [InlineData("/api/projects")]
    [InlineData("/mcp")]
    [InlineData("/_blazor")]
    [InlineData("/uploads/private.png")]
    public async Task Denied_peers_cannot_reach_login_or_endpoints_or_spoof_forwarded_headers(string path)
    {
        using TempDir directory = new();
        var access = new NetworkAccessService(directory.Path, new EphemeralDataProtectionProvider(), 5233);
        access.SetNetworkRules(true, "192.168.1.42");
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.43");
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("pc.test");
        context.Request.Path = path;
        context.Request.Headers["X-Forwarded-For"] = "192.168.1.42";
        context.Request.Headers["CF-Connecting-IP"] = "192.168.1.42";
        var middleware = new NetworkAccessMiddleware(_ => throw new InvalidOperationException("Denied peer reached endpoint"));
        await middleware.InvokeAsync(context, access, null!, null!);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task Empty_allowlist_preserves_local_recovery()
    {
        using TempDir directory = new();
        var access = new NetworkAccessService(directory.Path, new EphemeralDataProtectionProvider(), 5233);
        access.SetNetworkRules(true, "");
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("localhost");
        var reached = false;
        var middleware = new NetworkAccessMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, access, null!, null!);
        Assert.True(reached);
    }
}
