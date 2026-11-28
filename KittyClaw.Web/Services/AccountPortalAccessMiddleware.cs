using KittyClaw.Core.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace KittyClaw.Web.Services;

/// <summary>Both opted-in remote listeners use the same centrally verified account.</summary>
public sealed class AccountPortalAccessMiddleware(RequestDelegate next)
{
    private const string ChallengeCookie = "__Host-KittyClawPortalChallenge";
    private const string SessionCookie = "__Host-KittyClawPortalSession";
    public async Task InvokeAsync(HttpContext context, AccountPortalAccessService portal, NetworkAccessService network,
        IAntiforgery antiforgery, LocalizationService l)
    {
        if (context.Features.Get<AgentApiGrant>() is { Valid: true }) { await next(context); return; }
        var callback = context.Request.Path == "/account-access/callback";
        var start = context.Request.Path == "/account-access";
        var remote = context.Features.Get<RemoteTunnelRequest>() is not null;
        if (!remote && NetworkAccessMiddleware.IsLocal(context))
        {
            if (start || callback) { context.Response.StatusCode = 403; return; }
            await next(context); return;
        }
        if (!context.Request.IsHttps || NetworkAccessMiddleware.IsCrossOriginBrowserRequest(context))
        { context.Response.StatusCode = 403; return; }
        var kind = remote ? "tunnel" : "network";
        var origin = new Uri($"{context.Request.Scheme}://{context.Request.Host}").GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        if (!remote && (!network.AccountAccessEnabled || !network.Listening || context.Connection.LocalPort != network.Port
            || !IsNetworkHost(context.Request.Host.Host, network) || !network.AllowsNetwork(context.Connection.RemoteIpAddress)))
        { context.Response.StatusCode = 403; return; }
        if (start || callback || context.Request.Path == "/access")
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
            try
            {
                await portal.RefreshAsync(context.RequestAborted);
                if (callback)
                {
                    if (context.Request.Query.Count != 2 || context.Request.Query["code"].Count != 1 || context.Request.Query["state"].Count != 1)
                        throw new AccountAssociationException();
                    var cookie = await portal.CompleteAsync(context.Request.Cookies[ChallengeCookie], context.Request.Query["state"].ToString(),
                        context.Request.Query["code"].ToString(), kind, origin, DateTimeOffset.UtcNow, context.RequestAborted);
                    context.Response.Cookies.Delete(ChallengeCookie, Options(TimeSpan.Zero));
                    context.Response.Cookies.Append(SessionCookie, cookie, Options(TimeSpan.FromHours(12)));
                    context.Response.Redirect("/");
                }
                else await Begin(context, portal, kind, origin);
            }
            catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException)
            {
                context.Response.StatusCode = 401;
                await NetworkAccessMiddleware.Page(context, l["AccountTitle"],
                    $"<p>{System.Net.WebUtility.HtmlEncode(l["AccountAccessFailed"])}</p><p><a href='/account-access'>{System.Net.WebUtility.HtmlEncode(l["AccountAccessRetry"])}</a></p><p><a href='{AccountAssociationService.PortalOrigin}/security'>{System.Net.WebUtility.HtmlEncode(l["AccountSecurity"])}</a></p>");
            }
            return;
        }
        var sessionCookie = context.Request.Cookies[SessionCookie];
        if (!await portal.ValidAsync(sessionCookie, kind, origin, context.RequestAborted))
        {
            if (portal.AvailableFor(kind) && HttpMethods.IsGet(context.Request.Method) && context.Request.Headers.Accept.ToString().Contains("text/html"))
            {
                try { await Begin(context, portal, kind, origin); }
                catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException)
                { context.Response.StatusCode = 401; await NetworkAccessMiddleware.Page(context, l["AccountTitle"], System.Net.WebUtility.HtmlEncode(l["AccountAccessFailed"])); }
                return;
            }
            context.Response.StatusCode = portal.AvailableFor(kind) ? 401 : 403; return;
        }
        if (context.Request.Path == "/access/logout")
        {
            if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 405; return; }
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
            await portal.RevokeAsync(sessionCookie, kind, origin, context.RequestAborted);
            context.Response.Cookies.Delete(SessionCookie, Options(TimeSpan.Zero));
            context.Response.Redirect(AccountAssociationService.PortalOrigin);
            return;
        }
        context.Features.Set(new AccountTunnelAuthenticated());
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        void Revoke() => context.Abort();
        void CheckNetwork() { if (!remote && !network.AllowsNetwork(context.Connection.RemoteIpAddress)) context.Abort(); }
        portal.SecurityChanged += Revoke;
        network.SessionsRevoked += Revoke;
        network.NetworkRulesChanged += CheckNetwork;
        var monitor = MonitorAsync(context, portal, sessionCookie, kind, origin, monitorCancellation.Token);
        try { await next(context); }
        finally
        {
            portal.SecurityChanged -= Revoke;
            network.SessionsRevoked -= Revoke;
            network.NetworkRulesChanged -= CheckNetwork;
            await monitorCancellation.CancelAsync();
            try { await monitor; } catch (OperationCanceledException) { }
        }
    }
    private static bool IsNetworkHost(string host, NetworkAccessService network) =>
        host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address) && network.Addresses.Contains(address);
    private static async Task Begin(HttpContext context, AccountPortalAccessService portal, string kind, string origin)
    {
        var challenge = await portal.BeginAsync(kind, origin, DateTimeOffset.UtcNow, context.RequestAborted);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Cookies.Append(ChallengeCookie, challenge.Cookie, Options(TimeSpan.FromMinutes(5)));
        context.Response.Redirect(challenge.Url);
    }
    private static CookieOptions Options(TimeSpan age) => new()
    { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = age, IsEssential = true };
    private static async Task MonitorAsync(HttpContext context, AccountPortalAccessService portal, string? cookie, string kind, string origin, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
            if (!await portal.ValidAsync(cookie, kind, origin, ct, revalidate: true)) { context.Abort(); return; }
    }
}
