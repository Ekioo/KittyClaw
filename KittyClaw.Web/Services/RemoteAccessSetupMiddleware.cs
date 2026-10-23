using System.Net;
using KittyClaw.Core.Services;
using Microsoft.AspNetCore.Antiforgery;
using QRCoder;

namespace KittyClaw.Web.Services;

/// <summary>Setup uses ordinary local HTTP requests, not remote-invokable circuit handlers.</summary>
public sealed class RemoteAccessSetupMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RemoteAccessStore store, RemoteAccessConnector connector,
        NetworkAccessService access, IAntiforgery antiforgery, LocalizationService l)
    {
        if (context.Request.Path != "/remote-setup") { await next(context); return; }
        if (!NetworkAccessMiddleware.IsLocal(context)) { context.Response.StatusCode = 403; return; }
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
                    await connector.ActivateAsync(form["invitation"].ToString(), context.RequestAborted);
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
            using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(data);
            content += $"<p><img width='220' height='220' src='data:image/png;base64,{Convert.ToBase64String(qr.GetGraphic(5))}' alt='{E(l["RemoteUrlQr"])}'></p>";
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
                    content += $"<p><label for='invitation'>{E(l["RemoteInvitation"])}</label><input id='invitation' name='invitation' type='password' autocomplete='off' spellcheck='false' pattern='[a-f0-9]{{64}}' minlength='64' maxlength='64' required></p>";
                content += $"<p><label><input type='checkbox' name='consent' value='yes' required> {E(l["RemoteConsent"])}</label></p><button>{E(l[snapshot.Registered ? "RemoteResume" : "RemoteActivate"])}</button>";
            }
            content += "</form>";
        }
        content += $"<p><a href='/remote-setup'>{E(l["RemoteRefresh"])}</a></p><p><a href='/settings'>{E(l["GlobalSettings"])}</a></p>";
        await NetworkAccessMiddleware.Page(context, l["RemoteTitle"], content);
    }
}
