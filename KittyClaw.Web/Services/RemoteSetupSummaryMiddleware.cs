using System.Net;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

public static class RemoteSetupSummaryExtensions
{
    public static IApplicationBuilder UseRemoteSetupSummaries(this IApplicationBuilder app) =>
        app.UseWhen(context => HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Path == "/account-setup" || context.Request.Path == "/access-setup")
            && !NetworkAccessMiddleware.IsLocal(context), branch =>
        {
            branch.UseMiddleware<AccountPortalAccessMiddleware>();
            branch.UseMiddleware<RemoteSetupSummaryMiddleware>();
        });
}

/// <summary>Authenticated remote browsers can inspect setup without changing local ownership or access.</summary>
public sealed class RemoteSetupSummaryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AccountAssociationService accounts, AccessModeService modes, LocalizationService l)
    {
        var accountPage = context.Request.Path == "/account-setup";
        if (!accountPage && context.Request.Path != "/access-setup") { await next(context); return; }
        if (!HttpMethods.IsGet(context.Request.Method) || context.Features.Get<AccountTunnelAuthenticated>() is null)
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        static string E(string text) => WebUtility.HtmlEncode(text);
        var content = $"<p>{E(l["RemoteSetupReadOnly"])}</p>";
        if (accountPage && accounts.Snapshot.Account is { } account)
        {
            content += $"<p>{E(l["AccountAssociated"])} <strong>{E(account.Name)}</strong></p>";
            if (!string.IsNullOrWhiteSpace(account.Email)) content += $"<p>{E(account.Email)}</p>";
            content += $"<p><a href='{AccountAssociationService.PortalOrigin}'>{E(l["AccountPortal"])}</a></p>";
        }
        else if (!accountPage)
        {
            content += $"<p>{E(l["AccessCurrent"])} <strong>{E(l["AccessMode" + modes.Mode])}</strong></p>";
            content += $"<p role='status'>{E(l[modes.StatusKey])}</p>";
        }
        content += $"<p><a href='{AccountAssociationService.PortalOrigin}/security'>{E(l["AccountSecurity"])}</a></p>";
        content += $"<p><a href='/settings'>{E(l["GlobalSettings"])}</a></p><p><a href='/'>{E(l["BackToProjects"])}</a></p>";
        context.Response.Headers.CacheControl = "no-store";
        await NetworkAccessMiddleware.Page(context, l[accountPage ? "AccountAssociationStatus" : "AccessConnectionStatus"], content);
    }
}
