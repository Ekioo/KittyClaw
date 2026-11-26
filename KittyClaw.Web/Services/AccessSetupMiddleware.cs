using System.Net;
using System.Security.Cryptography;
using KittyClaw.Core.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace KittyClaw.Web.Services;

/// <summary>Local-only controller for the single three-mode access preference.</summary>
public sealed class AccessSetupMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path != "/access-setup" && !path.StartsWithSegments("/network-setup") && path != "/remote-setup")
        { await next(context); return; }
        if (!NetworkAccessMiddleware.IsLocal(context) || NetworkAccessMiddleware.IsCrossOriginBrowserRequest(context))
        { context.Response.StatusCode = 403; return; }
        if (path != "/access-setup")
        {
            if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 410; return; }
            context.Response.Redirect("/access-setup"); return;
        }
        var services = context.RequestServices;
        var modes = services.GetRequiredService<AccessModeService>();
        var network = services.GetRequiredService<NetworkAccessService>();
        var remote = services.GetRequiredService<RemoteAccessStore>();
        var connector = services.GetRequiredService<RemoteAccessConnector>();
        var accounts = services.GetRequiredService<AccountAssociationService>();
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var l = services.GetRequiredService<LocalizationService>();
        static string E(string value) => WebUtility.HtmlEncode(value);
        context.Response.Headers.CacheControl = "no-store";
        string? error = null, notice = null;
        InstanceAccessMode? selected = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
            var form = await context.Request.ReadFormAsync();
            if (form["action"] == "network-rules")
            {
                try { network.SetNetworkRules(form["restrict"] == "on", form["networks"].ToString()); notice = l["NetworkRulesSaved"]; }
                catch (ArgumentException) { error = l["NetworkRulesInvalid"]; }
            }
            else if (form["action"] == "mode")
            {
                selected = form["mode"].ToString() switch
                { "local" => InstanceAccessMode.Local, "network" => InstanceAccessMode.Network, "internet" => InstanceAccessMode.Internet, _ => null };
                if (selected is null) { context.Response.StatusCode = 400; return; }
                try
                {
                    await modes.ApplyAsync(selected.Value, form["consent"] == "yes", form["subdomain"].ToString(), context.RequestAborted);
                    context.Response.Redirect(form["return"] == "/settings" ? "/settings" : "/access-setup"); return;
                }
                catch (RemoteAccessException ex) { error = l[ex.Code]; }
            }
            else { context.Response.StatusCode = 400; return; }
            if (error is not null) context.Response.StatusCode = 400;
        }
        else if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        // Refresh presentation, but an outage never blocks local-only recovery.
        try { await accounts.RefreshAsync(context.RequestAborted); }
        catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException)
        { error ??= l["AccountError"]; }
        string Token() => $"<input type='hidden' name='__RequestVerificationToken' value='{E(antiforgery.GetAndStoreTokens(context).RequestToken!)}'>";
        var content = $"<style>{AccessSetupView.Styles}</style><p>{E(l["AccountNetworkHint"])}</p>";
        if (error is not null) content += $"<p role='alert'>{E(error)}</p>";
        if (notice is not null) content += $"<p role='status'>{E(notice)}</p>";
        content += $"<div class='access-status' role='status'>{E(l["AccessCurrent"])} <strong>{E(l["AccessMode" + modes.Mode])}</strong></div>";
        content += $"<p>{E(l[modes.AccountReady ? "AccountNetworkReady" : "AccountNetworkRequired"])}</p><p><a href='{AccountAssociationService.PortalOrigin}/security'>{E(l["AccountSecurity"])}</a></p>";
        if (accounts.Snapshot.Account is null) content += $"<p><a href='/account-setup'>{E(l["AccountConfigure"])}</a></p>";
        content += $"<form class='access-settings-form' method='post' action='/access-setup' data-current-mode='{modes.Mode.ToString().ToLowerInvariant()}' novalidate>{Token()}<input type='hidden' name='action' value='mode'>{AccessSetupView.Options(selected ?? modes.Mode, remote, l)}<noscript><button>{E(l["AccessApply"])}</button></noscript></form>";
        if (modes.Mode == InstanceAccessMode.Network)
        {
            content += $"<p>{E(l[network.Listening ? "NetworkEnabled" : "NetworkRestart"])}</p>";
            foreach (var address in network.Addresses)
            {
                var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
                var url = $"https://{host}:{network.Port}"; content += $"<p><a href='{E(url)}'>{E(url)}</a></p>";
            }
            using var certificate = network.Certificate();
            content += $"<p>{E(l["NetworkCertificate"])}</p><p>SHA-256: <code>{certificate.GetCertHashString(HashAlgorithmName.SHA256)}</code></p>";
        }
        if (modes.Mode == InstanceAccessMode.Internet)
        {
            var status = !connector.CanAuthenticate ? "AuthenticationRequired" : connector.NeedsRestart ? "RestartRequired" : connector.Status.State;
            content += $"<p role='status'>{E(l["RemoteStatus" + status])}</p>";
            if (connector.Status.Error is { } connectionError) content += $"<p role='alert'>{E(l[connectionError])}</p>";
            if (remote.Snapshot.Url is { } url)
                content += $"<p><a href='{E(url)}' target='_blank' rel='noopener noreferrer'>{E(url)}</a></p><img width='200' height='200' style='max-width:100%;height:auto' src='{RemoteAccessQrCode.DataUrl(url)}' alt='{E(l["RemoteUrlQr"])}'>";
        }
        content += $"<details class='setup-advanced'><summary>{E(l["NetworkRulesTitle"])}</summary><section class='network-rules'><p>{E(l["NetworkRulesHint"])}</p><form method='post' action='/access-setup'>{Token()}<input type='hidden' name='action' value='network-rules'>" +
            $"<label class='network-rules-toggle'><input type='checkbox' name='restrict' {(network.RestrictNetwork ? "checked" : "")}><span>{E(l["NetworkRulesRestrict"])}</span></label><label for='allowed-networks'>{E(l["NetworkRulesEntries"])}</label>" +
            $"<textarea id='allowed-networks' name='networks' rows='6' maxlength='16384' spellcheck='false' autocapitalize='off'>{E(string.Join("\n", network.AllowedNetworks))}</textarea><button>{E(l["NetworkRulesSave"])}</button></form></section></details>";
        content += $"<p><a href='/access-setup'>{E(l["RemoteRefresh"])}</a></p><p><a href='/settings'>{E(l["GlobalSettings"])}</a></p>";
        await NetworkAccessMiddleware.Page(context, l["AccessTitle"], content, clientScript: AccessSetupView.Script);
    }
}
