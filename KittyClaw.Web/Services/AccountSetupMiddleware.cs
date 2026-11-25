using System.Net;
using KittyClaw.Core.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace KittyClaw.Web.Services;

/// <summary>Only a same-origin, loopback browser can initiate and confirm ownership.</summary>
public sealed class AccountSetupMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AccountAssociationService accounts, IAntiforgery antiforgery, LocalizationService l)
    {
        if (!context.Request.Path.StartsWithSegments("/account-setup")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        if (!NetworkAccessMiddleware.IsLocal(context) || NetworkAccessMiddleware.IsCrossOriginBrowserRequest(context))
        { context.Response.StatusCode = 403; return; }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsPost(context.Request.Method))
        { context.Response.StatusCode = 405; return; }
        var error = false;
        try
        {
            if (HttpMethods.IsPost(context.Request.Method))
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
                var fields = await context.Request.ReadFormAsync(context.RequestAborted);
                switch (fields["action"].ToString())
                {
                    case "start":
                        await accounts.BeginAssociationAsync(context.RequestAborted);
                        context.Response.Redirect(ContinuationUrl(context, accounts));
                        return;
                    case "continue":
                        context.Response.Redirect(ContinuationUrl(context, accounts));
                        return;
                    case "confirm": await accounts.ConfirmAsync(fields["accountId"].ToString(), context.RequestAborted); break;
                    case "disconnect": await accounts.DisconnectAsync(context.RequestAborted); break;
                    default: context.Response.StatusCode = 400; return;
                }
                context.Response.Redirect("/account-setup");
                return;
            }
            if (accounts.Snapshot.Account is not null || accounts.Snapshot.AuthorizationUrl is not null)
                await accounts.RefreshAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is AccountAssociationException or HttpRequestException or TaskCanceledException)
        { error = true; }

        var snapshot = accounts.Snapshot;
        var token = antiforgery.GetAndStoreTokens(context);
        string Form(string action, string label, string fields = "", string buttonClass = "") =>
            $"<form method='post' action='/account-setup'><input type='hidden' name='{E(token.FormFieldName)}' value='{E(token.RequestToken!)}'><input type='hidden' name='action' value='{action}'>{fields}<button class='{buttonClass}'>{E(label)}</button></form>";
        var content = $"<p>{E(l["AccountHint"])}</p>";
        if (error) content += $"<p role='alert'>{E(l["AccountError"])}</p>";
        if (snapshot.Account is { } account)
        {
            content += $"<p role='status'>{E(l["AccountAssociated"])} <strong>{E(account.Name)}</strong></p><p>{E(account.Email ?? "")}</p>" +
                $"<p><a href='{AccountAssociationService.PortalOrigin}'>{E(l["AccountPortal"])}</a></p>" +
                $"<details class='setup-advanced'><summary>{E(l["AccountDisconnect"])}</summary><p>{E(l["AccountDisconnectHint"])}</p>" +
                Form("disconnect", l["AccountDisconnect"], buttonClass: "danger") + "</details>";
        }
        else if (snapshot.ProposedAccount is { } proposed)
        {
            content += $"<h2>{E(l["AccountConfirmTitle"])}</h2><p><strong>{E(proposed.Name)}</strong><br>{E(proposed.Email ?? "")}</p><p>{E(l["AccountConfirmHint"])}</p>" +
                Form("confirm", l["AccountConfirm"], $"<input type='hidden' name='accountId' value='{E(proposed.Id)}'>") + Form("disconnect", l["AccountCancel"], buttonClass: "secondary");
        }
        else if (snapshot.AuthorizationUrl is not null && snapshot.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            content += Form("continue", l["AccountContinue"]) + $"<p>{E(l["AccountReturnHint"])}</p>" + Form("disconnect", l["AccountCancel"], buttonClass: "secondary");
        }
        else
        {
            if (snapshot.Revoked) content += $"<p>{E(l["AccountRevoked"])}</p>";
            content += Form("start", l["AccountAssociate"]);
        }
        content += $"<p>{E(l["AccountNetworkUnchanged"])}</p>";
        await NetworkAccessMiddleware.Page(context, l["AccountTitle"], content, AccountAssociationService.PortalOrigin);
    }
    private static string ContinuationUrl(HttpContext context, AccountAssociationService accounts)
    {
        if (accounts.Snapshot.AuthorizationUrl is not { } url || accounts.Snapshot.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new AccountAssociationException();
        // The loopback Host/peer boundary above protects this callback origin.
        // Only a read-only return page is allowed; ownership still requires local POST confirmation.
        var localReturn = $"{context.Request.Scheme}://{context.Request.Host}/account-setup";
        return $"{url}&local_return={Uri.EscapeDataString(localReturn)}";
    }
    private static string E(string value) => WebUtility.HtmlEncode(value);
}
