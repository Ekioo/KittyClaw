using System.Net;
using System.Net.Sockets;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Shared HttpClients for the httpRequest automation action (ticket #137).
/// The guarded client enforces the SSRF policy at CONNECT time — not just at URL-parse time,
/// which DNS rebinding would bypass: every address the host resolves to is validated, and
/// loopback / link-local (incl. the 169.254.169.254 cloud metadata range) / wildcard /
/// multicast targets are refused. Redirects are disabled on both clients so a 3xx cannot
/// bounce a request to a blocked target or a different scheme.
/// </summary>
internal static class HttpActionClient
{
    internal const int MaxResponseBytes = 64 * 1024;

    internal static readonly HttpClient Guarded = Create(guard: true);
    internal static readonly HttpClient Unguarded = Create(guard: false);

    private static HttpClient Create(bool guard)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (guard) handler.ConnectCallback = GuardedConnectAsync;
        // Per-request timeouts are enforced with a linked CTS in ActionExecutor.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> GuardedConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(ip => !IsBlockedTarget(ip)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(
                $"httpRequest: target '{host}' only resolves to blocked (local) addresses; set allowLocalTargets to opt in.");

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last ?? new HttpRequestException($"httpRequest: could not connect to '{host}'.");
    }

    /// <summary>Loopback, link-local (v4 169.254/16 incl. cloud metadata, v6 fe80::/10),
    /// wildcard, broadcast and multicast addresses are refused by the guarded client.</summary>
    internal static bool IsBlockedTarget(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return true;                          // link-local + metadata
            if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return true; // broadcast
            if (b[0] >= 224 && b[0] <= 239) return true;                          // multicast
        }
        else if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
        {
            return true;
        }
        return false;
    }
}
