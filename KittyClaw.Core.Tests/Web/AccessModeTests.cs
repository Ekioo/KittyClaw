using System.Net;
using System.Net.Http.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Tests.Web;

public sealed class AccessModeTests
{
    [Fact]
    public async Task Modes_are_exclusive_and_tunnel_address_survives_pause_without_another_allocation()
    {
        using var f = new Fixture();
        Assert.Equal(InstanceAccessMode.Local, f.Modes.Mode);
        await f.Modes.ApplyAsync(InstanceAccessMode.Network, false, null, default);
        Assert.True(f.Network.AccountAccessEnabled); Assert.False(f.Remote.Snapshot.Enabled); Assert.Equal(0, f.Broker.Registrations);
        await f.Modes.ApplyAsync(InstanceAccessMode.Internet, true, "office", default);
        Assert.Equal(InstanceAccessMode.Internet, f.Modes.Mode); Assert.False(f.Network.Enabled);
        var address = f.Remote.Snapshot.Url;
        Assert.Equal("https://office.kittyclaw.dev", address); Assert.Equal(1, f.Broker.Registrations);
        await f.Modes.ApplyAsync(InstanceAccessMode.Network, false, null, default);
        Assert.True(f.Network.AccountAccessEnabled); Assert.False(f.Remote.Snapshot.Enabled); Assert.Equal(address, f.Remote.Snapshot.Url);
        var generation = f.Network.AccessGeneration;
        await f.Modes.ApplyAsync(InstanceAccessMode.Network, false, null, default);
        Assert.Equal(generation, f.Network.AccessGeneration);
        await f.Modes.ApplyAsync(InstanceAccessMode.Internet, true, null, default);
        Assert.Equal(1, f.Broker.Registrations); Assert.Equal(address, f.Remote.Snapshot.Url);
        await f.Modes.ApplyAsync(InstanceAccessMode.Local, false, null, default);
        Assert.False(f.Network.Enabled); Assert.False(f.Remote.Snapshot.Enabled);
        Assert.Equal(address, f.Remote.Snapshot.Url);
    }

    [Fact]
    public async Task Internet_requires_the_account_factor_and_explicit_Cloudflare_consent_before_allocation()
    {
        using var f = new Fixture();
        var consent = await Assert.ThrowsAsync<RemoteAccessException>(() => f.Modes.ApplyAsync(InstanceAccessMode.Internet, false, null, default));
        Assert.Equal("AccessConsentRequired", consent.Code); Assert.Null(f.Remote.Identity); Assert.Equal(0, f.Broker.Registrations);
        f.AccountHandler.Mfa = false;
        foreach (var mode in new[] { InstanceAccessMode.Network, InstanceAccessMode.Internet })
        {
            var factor = await Assert.ThrowsAsync<RemoteAccessException>(() => f.Modes.ApplyAsync(mode, true, null, default));
            Assert.Equal("AccountNetworkRequired", factor.Code);
        }
        Assert.False(f.Network.Enabled); Assert.Null(f.Remote.Identity);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Modes.ApplyAsync((InstanceAccessMode)99, true, null, default));
    }

    [Fact]
    public async Task Failed_provisioning_preserves_LAN_and_local_only_recovery_works_during_portal_outage()
    {
        using var f = new Fixture();
        await f.Modes.ApplyAsync(InstanceAccessMode.Network, false, null, default);
        var generation = f.Network.AccessGeneration;
        f.Broker.Fail = true;
        await Assert.ThrowsAsync<RemoteAccessException>(() => f.Modes.ApplyAsync(InstanceAccessMode.Internet, true, "office", default));
        Assert.Equal(InstanceAccessMode.Network, f.Modes.Mode); Assert.False(f.Remote.Snapshot.Enabled);
        Assert.Equal(generation, f.Network.AccessGeneration);
        f.Broker.Fail = false;
        await f.Modes.ApplyAsync(InstanceAccessMode.Internet, true, "office", default);
        f.AccountHandler.Fail = true;
        await f.Modes.ApplyAsync(InstanceAccessMode.Local, false, null, default);
        Assert.False(f.Remote.Snapshot.Enabled); Assert.False(f.Network.Enabled);
    }

    [Fact]
    public async Task Concurrent_owner_changes_cannot_leave_both_remote_surfaces_enabled()
    {
        using var f = new Fixture();
        await Task.WhenAll(f.Modes.ApplyAsync(InstanceAccessMode.Internet, true, "office", default),
            f.Modes.ApplyAsync(InstanceAccessMode.Network, false, null, default));
        Assert.True(f.Network.AccountAccessEnabled); Assert.False(f.Remote.Snapshot.Enabled);
        Assert.Equal(InstanceAccessMode.Network, f.Modes.Mode);
    }

    [Fact]
    public async Task Legacy_dual_opt_ins_migrate_to_Internet_without_allocating_another_tunnel()
    {
        using var f = new Fixture();
        await f.Modes.ApplyAsync(InstanceAccessMode.Internet, true, "office", default);
        f.Network.EnableAccountAccess();
        var migrated = new AccessModeService(f.Network, f.Remote, f.Connector, f.Accounts);
        Assert.Equal(InstanceAccessMode.Internet, migrated.Mode);
        Assert.False(f.Network.Enabled); Assert.True(f.Remote.Snapshot.Enabled); Assert.Equal(1, f.Broker.Registrations);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _directory = new();
        private readonly HttpClient _accountsHttp, _brokerHttp;
        public NetworkAccessService Network { get; }
        public RemoteAccessStore Remote { get; }
        public AccountHandler AccountHandler { get; } = new();
        public BrokerHandler Broker { get; }
        public AccountAssociationService Accounts { get; }
        public RemoteAccessConnector Connector { get; }
        public AccessModeService Modes { get; }
        public Fixture()
        {
            var protection = new EphemeralDataProtectionProvider();
            Network = new(_directory.Path, protection, 5233); Remote = new(_directory.Path, protection);
            _accountsHttp = new(AccountHandler); Accounts = new(_directory.Path, protection, _accountsHttp);
            Accounts.BeginAssociationAsync(default).GetAwaiter().GetResult(); Accounts.RefreshAsync(default).GetAwaiter().GetResult();
            Accounts.ConfirmAsync(AccessModeTests.AccountHandler.Owner.Id, default).GetAwaiter().GetResult(); Accounts.RefreshAsync(default).GetAwaiter().GetResult();
            Broker = new(Remote); _brokerHttp = new(Broker);
            Connector = new(Remote, new(_brokerHttp, new Uri("https://remote-api.kittyclaw.dev")), new Runtime(), new(false,5234,string.Empty), Network, new Lifetime(), Accounts);
            Modes = new(Network, Remote, Connector, Accounts);
        }
        public void Dispose() { Connector.Dispose(); Accounts.Dispose(); _accountsHttp.Dispose(); _brokerHttp.Dispose(); _directory.Dispose(); }
    }
    private sealed class AccountHandler : HttpMessageHandler
    {
        public static readonly AssociatedAccount Owner = new("f8e66066-4b2e-41d4-8e48-96e3204ee113", "Owner", null);
        public bool Mfa = true, Fail;
        private bool _linked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            object body = request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "associate" => new { authorizationUrl = AccountAssociationService.PortalOrigin + "/associate?request=" + new string('a',64), expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() },
                "status" => new { account = _linked ? Owner : null, proposedAccount = _linked ? null : Owner, revoked = false, mfaEnabled = Mfa },
                "confirm" => new { associated = _linked = true }, _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
    private sealed class BrokerHandler(RemoteAccessStore store) : HttpMessageHandler
    {
        public int Registrations;
        public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Registrations++; await Task.Delay(10,ct);
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { instanceId = store.Identity!.InstanceId,
                url = "https://" + RemoteAccessName.Hostname(store.Identity), state = "pending", originPort = store.Identity.Port }) };
        }
    }
    private sealed class Runtime : ICloudflaredRuntime
    { public Task RunAsync(string token, Action<bool> ready, CancellationToken ct) => throw new InvalidOperationException("No live tunnel is allowed in this test."); }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
