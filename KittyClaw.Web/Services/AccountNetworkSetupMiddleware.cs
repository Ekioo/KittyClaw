using System.Net;
using System.Security.Cryptography;
using KittyClaw.Core.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace KittyClaw.Web.Services;

/// <summary>Local opt-in and IP rules; the account portal owns the only authenticator.</summary>
public sealed class AccountNetworkSetupMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, NetworkAccessService network, AccountAssociationService accounts,
        IAntiforgery antiforgery, LocalizationService l)
    {
        if (!context.Request.Path.StartsWithSegments("/network-setup")) { await next(context); return; }
        if (!NetworkAccessMiddleware.IsLocal(context) || NetworkAccessMiddleware.IsCrossOriginBrowserRequest(context))
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers.CacheControl = "no-store";
        static string E(string value) => WebUtility.HtmlEncode(value);
        var message = "";
        var refreshed = false;
        try { await accounts.RefreshAsync(context.RequestAborted); refreshed = true; }
        catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException)
        { message = $"<p role='alert'>{E(l["AccountError"])}</p>"; }
        var ready = refreshed && accounts.Snapshot is { Account: not null, MfaEnabled: true };
        if (HttpMethods.IsPost(context.Request.Method))
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
            var form = await context.Request.ReadFormAsync();
            switch (form["action"].ToString())
            {
                case "enable" when ready:
                    network.EnableAccountAccess(); context.Response.Redirect("/network-setup"); return;
                case "disable":
                    network.Disable(); context.Response.Redirect("/network-setup"); return;
                case "network-rules":
                    try { network.SetNetworkRules(form["restrict"] == "on", form["networks"].ToString()); message += $"<p role='status'>{E(l["NetworkRulesSaved"])}</p>"; }
                    catch (ArgumentException) { context.Response.StatusCode = 400; message += $"<p role='alert'>{E(l["NetworkRulesInvalid"])}</p>"; }
                    break;
                default: context.Response.StatusCode = 400; break;
            }
        }
        else if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        string Token() => $"<input type='hidden' name='__RequestVerificationToken' value='{E(antiforgery.GetAndStoreTokens(context).RequestToken!)}'>";
        var content = $"<p>{E(l["AccountNetworkHint"])}</p>{message}";
        content += $"<p role='status'>{E(l[ready ? "AccountNetworkReady" : "AccountNetworkRequired"])}</p>";
        if (accounts.Snapshot.Account is { } account) content += $"<p>{E(l["AccountAssociated"])} <strong>{E(account.Name)}</strong></p>";
        content += $"<p><a href='{AccountAssociationService.PortalOrigin}/security'>{E(l["AccountSecurity"])}</a></p>";
        if (accounts.Snapshot.Account is null) content += $"<p><a href='/account-setup'>{E(l["AccountConfigure"])}</a></p>";
        if (network.AccountAccessEnabled)
        {
            content += $"<p>{E(l[network.Listening ? "NetworkEnabled" : "NetworkRestart"])}</p>";
            if (ready)
            {
                foreach (var address in network.Addresses)
                {
                    var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
                    var url = $"https://{host}:{network.Port}";
                    content += $"<p><a href='{E(url)}'>{E(url)}</a></p>";
                }
            }
            content += $"<form method='post' action='/network-setup'>{Token()}<input type='hidden' name='action' value='disable'><button>{E(l["NetworkDisable"])}</button></form>";
            using var certificate = network.Certificate();
            content += $"<p>{E(l["NetworkCertificate"])}</p><p>SHA-256: <code>{certificate.GetCertHashString(HashAlgorithmName.SHA256)}</code></p>";
        }
        else if (ready)
            content += $"<p>{E(l["AccountNetworkEnableHint"])}</p><form method='post' action='/network-setup'>{Token()}<input type='hidden' name='action' value='enable'><button>{E(l["AccountNetworkEnable"])}</button></form>";
        content += $"<section class='network-rules' aria-labelledby='network-rules-title'><h2 id='network-rules-title'>{E(l["NetworkRulesTitle"])}</h2><p>{E(l["NetworkRulesHint"])}</p>" +
            $"<form method='post' action='/network-setup#network-rules-title'>{Token()}<input type='hidden' name='action' value='network-rules'>" +
            $"<label class='network-rules-toggle'><input type='checkbox' name='restrict' {(network.RestrictNetwork ? "checked" : "")}><span>{E(l["NetworkRulesRestrict"])}</span></label>" +
            $"<label for='allowed-networks'>{E(l["NetworkRulesEntries"])}</label><textarea id='allowed-networks' name='networks' rows='6' maxlength='16384' spellcheck='false' autocapitalize='off'>{E(string.Join("\n", network.AllowedNetworks))}</textarea><button>{E(l["NetworkRulesSave"])}</button></form></section>";
        content += $"<p><a href='/network-setup'>{E(l["RemoteRefresh"])}</a></p><p><a href='/remote-setup'>{E(l["RemoteConfigure"])}</a></p><p><a href='/settings'>{E(l["GlobalSettings"])}</a></p>";
        await NetworkAccessMiddleware.Page(context, l["NetworkTitle"], content);
    }
}
