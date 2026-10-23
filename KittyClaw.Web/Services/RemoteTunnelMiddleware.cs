using System.Net;
using System.Text.RegularExpressions;

namespace KittyClaw.Web.Services;

/// <summary>Explicit opt-in origin listener reserved for a managed HTTPS tunnel.</summary>
public sealed record RemoteTunnelOptions(bool Enabled = false, int Port = 5234, string Hostname = "")
{
    public static RemoteTunnelOptions Read(IConfiguration configuration)
    {
        var options = new RemoteTunnelOptions(
            configuration.GetValue("RemoteTunnel:Enabled", false),
            configuration.GetValue("RemoteTunnel:Port", 5234),
            configuration["RemoteTunnel:Hostname"] ?? "");
        if (!options.Enabled) return options;
        if (options.Port is < 1024 or > 65535)
            throw new InvalidOperationException("RemoteTunnel:Port must be between 1024 and 65535.");
        if (!Regex.IsMatch(options.Hostname, @"^kc-[a-z0-9]{6,40}\.kittyclaw\.dev$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("RemoteTunnel:Hostname must be an assigned kc-<instance>.kittyclaw.dev hostname.");
        return options;
    }
}

/// <summary>Marks the listener, never a client header, as the remote trust boundary.</summary>
internal sealed class RemoteTunnelRequest;

public sealed class RemoteTunnelMiddleware(RequestDelegate next, RemoteTunnelOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Enabled || context.Connection.LocalPort != options.Port)
        {
            // An assigned public host must never reach the trusted local listener.
            if (options.Enabled && context.Request.Host.Host.Equals(options.Hostname, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
            return;
        }

        context.Features.Set(new RemoteTunnelRequest());
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null || !IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)
            || !context.Request.Host.Host.Equals(options.Hostname, StringComparison.OrdinalIgnoreCase)
            || context.Request.Host.Port is not (null or 443))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Only this dedicated loopback origin represents a configured HTTPS edge.
        // Do not derive trust or the public origin from X-Forwarded-* headers.
        context.Request.Scheme = "https";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        // The first release exposes the browser UI only. Agent APIs stay local.
        if (context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/mcp")
            || context.Request.Path.StartsWithSegments("/openapi"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }
}
