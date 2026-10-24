using System.Net;
using KittyClaw.Core.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace KittyClaw.Web.Services;

/// <summary>Setup uses ordinary local HTTP requests, not remote-invokable circuit handlers.</summary>
public sealed class RemoteAccessSetupMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RemoteAccessStore store, RemoteAccessConnector connector,
        NetworkAccessService access, IAntiforgery antiforgery, LocalizationService l)
    {
        if (context.Request.Path != "/remote-setup") { await next(context); return; }
        if (!NetworkAccessMiddleware.IsLocal(context) || NetworkAccessMiddleware.IsCrossOriginBrowserRequest(context)) { context.Response.StatusCode = 403; return; }
        context.Response.Headers.CacheControl = "no-store";
        string? error = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
            var form = await context.Request.ReadFormAsync();
            try
            {
                if (form["action"] == "pause") await connector.PauseAsync(context.RequestAborted);
                else if (form["action"] == "activate" && form["consent"] == "yes")
                    await connector.ActivateAsync(form["invitation"].ToString(), context.RequestAborted, form["subdomain"].ToString());
                else { context.Response.StatusCode = 400; return; }
            }
            catch (RemoteAccessException ex) { error = l[ex.Code]; }
            if (error is null) { context.Response.Redirect("/remote-setup"); return; }
            context.Response.StatusCode = 400;
        }
        else if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }

        static string E(string text) => WebUtility.HtmlEncode(text);
        var snapshot = store.Snapshot;
        var status = !snapshot.Enabled ? "Disabled" : !access.Enabled ? "AuthenticationRequired"
            : connector.NeedsRestart ? "RestartRequired" : connector.Status.State;
        var content = $"<p>{E(l["RemoteHint"])}</p><p role='status'><strong>{E(l["RemoteStatus" + status])}</strong></p>";
        if (error is not null) content += $"<p role='alert'>{E(error)}</p>";
        if (connector.Status.Error is { } connectorError && snapshot.Enabled)
            content += $"<p role='alert'>{E(l[connectorError])}</p>";
        if (snapshot.Url is { } url)
        {
            content += $"<p><a href='{E(url)}' target='_blank' rel='noreferrer'>{E(url)}</a></p>";
            content += $"<figure style='margin:24px 0'><a href='{E(url)}' target='_blank' rel='noopener noreferrer'><img width='256' height='256' style='display:block;max-width:100%;height:auto;background:white;border-radius:8px' src='{RemoteAccessQrCode.DataUrl(url)}' alt='{E(l["RemoteUrlQr"])}' data-testid='remote-url-qr'></a><figcaption><h2 style='font-size:1.1rem'>{E(l["RemoteQrTitle"])}</h2><p>{E(l["RemoteQrInstructions"])}</p><p>{E(l["RemoteQrAuthentication"])}</p></figcaption></figure>";
        }
        if (!access.Enabled)
            content += $"<p>{E(l["RemoteAuthenticationRequired"])}</p><p><a href='/network-setup'>{E(l["NetworkConfigure"])}</a></p>";
        if (snapshot.Enabled || access.Enabled)
        {
            var csrf = antiforgery.GetAndStoreTokens(context).RequestToken!;
            content += $"<form method='post' action='/remote-setup'><input type='hidden' name='__RequestVerificationToken' value='{E(csrf)}'>";
            if (snapshot.Enabled)
                content += $"<input type='hidden' name='action' value='pause'><button>{E(l["RemotePause"])}</button>";
            else
            {
                content += "<input type='hidden' name='action' value='activate'>";
                if (!snapshot.Registered)
                {
                    var pending = store.Identity;
                    var readOnly = pending is not null && !pending.NameRetryAllowed ? " readonly" : "";
                    content += $"<p><label for='subdomain'>{E(l["RemoteName"])}</label><input id='subdomain' name='subdomain' value='{E(pending?.Subdomain ?? "")}' autocomplete='off' autocapitalize='none' spellcheck='false' pattern='[A-Za-z0-9][A-Za-z0-9-]{{1,30}}[A-Za-z0-9]' minlength='3' maxlength='32'{readOnly}><span>.kittyclaw.dev</span></p><p>{E(l["RemoteNameHint"])}</p>";
                    content += $"<p><label for='invitation'>{E(l["RemoteInvitation"])}</label><input id='invitation' name='invitation' type='password' autocomplete='off' spellcheck='false' pattern='[a-f0-9]{{64}}' minlength='64' maxlength='64' required></p>";
                }
                content += $"<p><label><input type='checkbox' name='consent' value='yes' required> {E(l["RemoteConsent"])}</label></p><button>{E(l[snapshot.Registered ? "RemoteResume" : "RemoteActivate"])}</button>";
            }
            content += "</form>";
        }
        content += $"<p><a href='/remote-setup'>{E(l["RemoteRefresh"])}</a></p><p><a href='/settings'>{E(l["GlobalSettings"])}</a></p>";
        await NetworkAccessMiddleware.Page(context, l["RemoteTitle"], content);
    }
}
