using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using KittyClaw.Core.Services;
using QRCoder;

namespace KittyClaw.Web.Services;

/// <summary>Runs before every endpoint, static upload and Blazor handshake.</summary>
public sealed class NetworkAccessMiddleware(RequestDelegate next)
{
    private const string Cookie = "__Host-KittyClawAccess";

    internal static bool IsLocal(HttpContext context)
    {
        if (context.Features.Get<RemoteTunnelRequest>() is not null) return false;
        var address = context.Connection.RemoteIpAddress;
        // In-process HTTP transports have no IP pair. Kestrel TCP connections always do.
        var localTransport = address is null ? context.Connection.LocalIpAddress is null
            : IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        return localTransport && (context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(context.Request.Host.Host.Trim('[', ']'), out var host) && IPAddress.IsLoopback(host));
    }

    internal static bool IsCrossOriginBrowserRequest(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}") return true;
        var site = context.Request.Headers["Sec-Fetch-Site"].ToString();
        return !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method) && site is "cross-site" or "same-site";
    }

    internal static string LoginSource(HttpContext context)
    {
        // Cloudflare overwrites this header at the edge. Trust it only on the
        // dedicated loopback tunnel origin, never on LAN or ordinary listeners.
        if (context.Features.Get<RemoteTunnelRequest>() is not null
            && IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].ToString(), out var edgePeer))
            return "tunnel:" + Normalize(edgePeer);
        return "peer:" + (context.Connection.RemoteIpAddress is { } peer ? Normalize(peer) : "in-process");

        static string Normalize(IPAddress address) =>
            (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    public async Task InvokeAsync(HttpContext context, NetworkAccessService access, IAntiforgery antiforgery, LocalizationService l)
    {
        // Local automation remains trusted, but an external browser is not local
        // automation merely because its form or WebSocket targets localhost.
        if (IsCrossOriginBrowserRequest(context)) { context.Response.StatusCode = 403; return; }
        var local = IsLocal(context);
        var setup = context.Request.Path.StartsWithSegments("/network-setup");
        var login = context.Request.Path == "/access";
        var logout = context.Request.Path == "/access/logout";
        if (setup)
        {
            if (!local) { context.Response.StatusCode = 403; return; }
            await Setup(context, access, antiforgery, l);
            return;
        }
        if (local)
        {
            if (logout)
            {
                if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = 405; return; }
                if (!await ValidForm(context, antiforgery)) return;
                context.Response.Redirect("/");
                return;
            }
            await next(context);
            return;
        }
        var directNetwork = context.Features.Get<RemoteTunnelRequest>() is null;
        if (directNetwork && !access.AllowsNetwork(context.Connection.RemoteIpAddress))
        { context.Response.StatusCode = 403; return; }
        if (!access.Enabled || !context.Request.IsHttps) { context.Response.StatusCode = 403; return; }

        if (login || logout)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsPost(context.Request.Method))
            {
                if (!await ValidForm(context, antiforgery)) return;
                if (logout)
                {
                    access.RevokeSession(context.Request.Cookies[Cookie], DateTimeOffset.UtcNow);
                    context.Response.Cookies.Delete(Cookie, new CookieOptions { Secure = true, Path = "/" });
                    context.Response.Redirect("/access");
                    return;
                }
                var form = await context.Request.ReadFormAsync();
                var session = access.Authenticate(form["code"].ToString(), DateTimeOffset.UtcNow, LoginSource(context));
                if (session is not null)
                {
                    context.Response.Cookies.Append(Cookie, session, new CookieOptions
                    {
                        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
                        Path = "/", MaxAge = TimeSpan.FromHours(12), IsEssential = true
                    });
                    context.Response.Redirect("/");
                    return;
                }
                context.Response.StatusCode = 401;
                await Page(context, l["NetworkLogin"], $"<p role='alert'>{E(l["NetworkInvalidCode"])}</p>" + LoginForm(context, antiforgery, l));
                return;
            }
            if (!HttpMethods.IsGet(context.Request.Method) || logout) { context.Response.StatusCode = 405; return; }
            await Page(context, l["NetworkLogin"], LoginForm(context, antiforgery, l));
            return;
        }

        if (!access.ValidSession(context.Request.Cookies[Cookie], DateTimeOffset.UtcNow))
        {
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Headers.Accept.ToString().Contains("text/html"))
                context.Response.Redirect("/access");
            else context.Response.StatusCode = 401;
            return;
        }
        // Close existing remote WebSocket/SignalR transports on reset or session expiry.
        using var expiration = new CancellationTokenSource();
        using var registration = expiration.Token.Register(context.Abort);
        void Revoke() => context.Abort();
        var sessionKey = NetworkAccessService.SessionKey(context.Request.Cookies[Cookie]!);
        void RevokeOne(string revokedKey) { if (revokedKey == sessionKey) context.Abort(); }
        void CheckNetwork() { if (directNetwork && !access.AllowsNetwork(context.Connection.RemoteIpAddress)) context.Abort(); }
        access.SessionsRevoked += Revoke;
        access.SessionRevoked += RevokeOne;
        access.NetworkRulesChanged += CheckNetwork;
        try
        {
            if (directNetwork && !access.AllowsNetwork(context.Connection.RemoteIpAddress))
            { context.Response.StatusCode = 403; return; }
            var expires = access.SessionExpiry(context.Request.Cookies[Cookie], DateTimeOffset.UtcNow);
            if (expires is null) { context.Response.StatusCode = 401; return; }
            expiration.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, (expires.Value - DateTimeOffset.UtcNow).TotalMilliseconds)));
            await next(context);
        }
        finally
        {
            access.SessionsRevoked -= Revoke;
            access.SessionRevoked -= RevokeOne;
            access.NetworkRulesChanged -= CheckNetwork;
        }
    }

    private static async Task Setup(HttpContext context, NetworkAccessService access, IAntiforgery antiforgery, LocalizationService l)
    {
        context.Response.Headers.CacheControl = "no-store";
        var message = "";
        var rulesMessage = "";
        string? rulesInput = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (!await ValidForm(context, antiforgery)) return;
            var form = await context.Request.ReadFormAsync();
            if (form["action"] == "disable") access.Disable();
            else if (form["action"] == "network-rules")
            {
                try
                {
                    access.SetNetworkRules(form["restrict"] == "on", form["networks"].ToString());
                    rulesMessage = $"<p role='status'>{E(l["NetworkRulesSaved"])}</p>";
                }
                catch (ArgumentException)
                {
                    context.Response.StatusCode = 400;
                    rulesMessage = $"<p role='alert'>{E(l["NetworkRulesInvalid"])}</p>";
                    rulesInput = form["networks"].ToString();
                }
            }
            else
            {
                try
                {
                    if (!access.Enable(form["enrollment"].ToString(), form["code"].ToString()))
                        message = $"<p role='alert'>{E(l["NetworkInvalidCode"])}</p>";
                }
                catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
                { message = $"<p role='alert'>{E(l["NetworkInvalidCode"])}</p>"; }
            }
        }
        else if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }

        var content = $"<p>{E(l["NetworkHint"])}</p>" + message;
        if (access.Enabled)
        {
            content += $"<p role='status'>{E(l[access.Listening ? "NetworkEnabled" : "NetworkRestart"])}</p>";
            foreach (var address in access.Addresses)
            {
                var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
                var url = $"https://{host}:{access.Port}";
                content += $"<p><a href='{E(url)}'>{E(url)}</a></p>";
            }
            content += $"<p>{E(l["NetworkCertificate"])}</p><form method='post' action='/network-setup'>{Token(context, antiforgery)}<input type='hidden' name='action' value='disable'><button>{E(l["NetworkDisable"])}</button></form>";
            using var certificate = access.Certificate();
            content += $"<p>SHA-256: <code>{certificate.GetCertHashString(HashAlgorithmName.SHA256)}</code></p>";
        }
        else
        {
            var enrollment = access.CreateEnrollment();
            var secret = access.EnrollmentSecret(enrollment);
            var uri = $"otpauth://totp/KittyClaw:{Uri.EscapeDataString(Environment.MachineName)}?secret={secret}&issuer=KittyClaw&algorithm=SHA1&digits=6&period=30";
            using var qrData = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(qrData);
            var qrImage = Convert.ToBase64String(qr.GetGraphic(6));
            content += $"<p>{E(l["NetworkEnroll"])}</p><p><img src='data:image/png;base64,{qrImage}' alt='{E(l["NetworkQrCode"])}' style='display:block;width:300px;max-width:100%;height:auto;image-rendering:pixelated'></p><p><code>{E(secret)}</code></p><p><a href='{E(uri)}'>{E(l["NetworkOpenApp"])}</a></p>" +
                $"<form method='post' action='/network-setup'>{Token(context, antiforgery)}<input type='hidden' name='enrollment' value='{E(enrollment)}'>{CodeInput(l, rulesMessage.Length == 0)}<button>{E(l["NetworkEnable"])}</button></form>";
        }
        content += $"<section class='network-rules' aria-labelledby='network-rules-title'><h2 id='network-rules-title' tabindex='-1' {(rulesMessage.Length > 0 ? "autofocus" : "")}>{E(l["NetworkRulesTitle"])}</h2>{rulesMessage}<p>{E(l["NetworkRulesHint"])}</p>" +
            $"<form method='post' action='/network-setup#network-rules-title'>{Token(context, antiforgery)}<input type='hidden' name='action' value='network-rules'>" +
            $"<label class='network-rules-toggle'><input type='checkbox' name='restrict' {(access.RestrictNetwork ? "checked" : "")}><span>{E(l["NetworkRulesRestrict"])}</span></label>" +
            $"<label for='allowed-networks'>{E(l["NetworkRulesEntries"])}</label><textarea id='allowed-networks' name='networks' rows='6' maxlength='16384' spellcheck='false' autocapitalize='off'>{E(rulesInput ?? string.Join("\n", access.AllowedNetworks))}</textarea>" +
            $"<button>{E(l["NetworkRulesSave"])}</button></form></section>";
        content += $"<p>{E(l["NetworkRecovery"])}</p><p><a href='/settings'>{E(l["GlobalSettings"])}</a></p>";
        await Page(context, l["NetworkTitle"], content);
    }

    private static string LoginForm(HttpContext c, IAntiforgery a, LocalizationService l) =>
        $"<p>{E(l["NetworkLoginHint"])}</p><form method='post' action='/access'>{Token(c, a)}{CodeInput(l)}<button>{E(l["NetworkLogin"])}</button></form>";

    private static string CodeInput(LocalizationService l, bool autofocus = true) => $"<label for='access-code'>{E(l["NetworkCode"])}</label><div class='code-entry'><input id='access-code' name='code' type='text' inputmode='numeric' autocomplete='one-time-code' pattern='[0-9]{{6}}' minlength='6' maxlength='6' required {(autofocus ? "autofocus" : "")}><div class='code-cells' aria-hidden='true'>{string.Concat(Enumerable.Repeat("<span></span>", 6))}</div></div>";

    // One semantic input preserves mobile autofill, paste and keyboard editing.
    private const string CodeScript = """
        document.querySelectorAll('.code-entry').forEach(entry => {
            const input = entry.querySelector('input');
            const cells = [...entry.querySelectorAll('.code-cells span')];
            const form = input.form;
            let submitting = false;
            entry.classList.add('enhanced');
            const render = () => {
                const active = Math.min(input.selectionStart ?? input.value.length, 5);
                cells.forEach((cell, i) => {
                    cell.textContent = input.value[i] || '';
                    cell.classList.toggle('active', document.activeElement === input && i === active);
                });
            };
            const update = () => {
                input.value = input.value.replace(/[^0-9]/g, '').slice(0, 6);
                render();
                if (input.value.length === 6 && !submitting) form.requestSubmit();
            };
            input.addEventListener('input', update);
            input.addEventListener('paste', event => {
                const code = event.clipboardData?.getData('text').replace(/[^0-9]/g, '');
                if (code?.length === 6) {
                    event.preventDefault();
                    input.value = code;
                    update();
                }
            });
            ['focus', 'blur', 'click', 'keyup', 'select'].forEach(name => input.addEventListener(name, render));
            form.addEventListener('submit', event => {
                if (submitting) { event.preventDefault(); return; }
                submitting = true;
                form.setAttribute('aria-busy', 'true');
                form.querySelectorAll('button').forEach(button => button.disabled = true);
                input.readOnly = true;
            });
            render();
        });
        """;
    private static string Token(HttpContext c, IAntiforgery a)
    {
        var token = a.GetAndStoreTokens(c);
        return $"<input type='hidden' name='{E(token.FormFieldName)}' value='{E(token.RequestToken!)}'>";
    }

    private static async Task<bool> ValidForm(HttpContext c, IAntiforgery a)
    {
        try { await a.ValidateRequestAsync(c); return true; }
        catch (AntiforgeryValidationException) { c.Response.StatusCode = 400; return false; }
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
    internal static async Task Page(HttpContext context, string title, string content)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        // Native window controls are injected into local documents, including setup pages.
        var local = IsLocal(context);
        var localAssets = local ? " 'self'" : "";
        var shellStyles = local ? "<link rel='stylesheet' href='/css/windows-desktop.css'>" : "";
        var header = local ? $"<header class='page-header setup-header'><a href='/settings'>← {E(context.RequestServices.GetRequiredService<LocalizationService>()["GlobalSettings"])}</a></header>" : "";
        context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{nonce}'; img-src data:{localAssets}; style-src 'unsafe-inline'{localAssets}; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Browsers can send Origin: null on form POSTs under no-referrer, which
        // conflicts with our same-origin validation before the code is checked.
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        await context.Response.WriteAsync($$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{E(title)}} — KittyClaw</title>
            {{shellStyles}}
            <style>.network-rules{margin-top:32px;padding-top:24px;border-top:1px solid var(--border);line-height:1.5}.network-rules h2{margin:0 0 16px;font-size:1.1rem;font-weight:600}.network-rules .network-rules-toggle{display:flex;align-items:flex-start;gap:12px;min-height:44px;box-sizing:border-box;padding:12px;background:var(--surface2);border:1px solid var(--border);border-radius:7px;cursor:pointer}.network-rules .network-rules-toggle input{display:block;flex:0 0 20px;width:20px;height:20px;min-height:20px;margin:2px 0 0;padding:0;accent-color:#22c55e}.network-rules textarea{display:block;width:100%;min-height:144px;box-sizing:border-box;padding:12px;background:var(--surface2);color:var(--text);border:1px solid var(--border);border-radius:7px;font:16px/1.5 ui-monospace,Consolas,monospace;resize:vertical}.network-rules textarea:focus-visible,.network-rules input:focus-visible,.network-rules button:focus-visible{outline:2px solid var(--focus-ring);outline-offset:3px}</style>
            <style>:root{--surface:#191928;--surface2:#232338;--surface3:#2e2e48;--text:#e4e4f0;--text-dim:#46466a;--border:#2e2e48;--focus-ring:#60a5fa}.setup-header{max-width:648px;margin:0 auto}.windows-desktop .desktop-window-header,.windows-desktop .desktop-window-header *{box-sizing:border-box}.windows-desktop .desktop-window-header{max-width:none}.windows-desktop .desktop-window-header button{margin:0}.windows-desktop .desktop-window-header a{padding-block:0}</style>
            <style>body{background:#05050a;color:#e4e4f0;font:16px system-ui;margin:0;padding:24px}main{max-width:600px;margin:5vh auto;padding:24px;background:#191928;border-radius:12px}a{color:#4ade80;overflow-wrap:anywhere}code{overflow-wrap:anywhere}label,input,button{display:block;margin:16px 0}input,button{font:inherit;padding:12px;border-radius:7px;max-width:100%;box-sizing:border-box}button{background:#22c55e;color:#05050a;border:0;cursor:pointer}h1{font-size:1.4rem}img,svg{max-width:100%;height:auto}@media(max-width:767px){body{padding:0;min-height:100dvh;background:#191928}main{margin:0;padding:24px 16px max(24px,env(safe-area-inset-bottom));border-radius:0;box-sizing:border-box;overflow-wrap:anywhere}input,button{width:100%;min-height:44px}a{display:inline-block;padding-block:10px}h1{font-size:1.3rem} }</style></head><body>{{header}}<main><h1>{{E(title)}}</h1>{{content}}</main>
            <style>.code-entry{position:relative;width:100%;max-width:320px;margin:20px 0}.code-cells{display:none}.code-entry.enhanced input{position:absolute;inset:0;width:100%;height:100%;margin:0;opacity:0;cursor:text}.enhanced .code-cells{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:8px;pointer-events:none}.code-cells span{display:flex;align-items:center;justify-content:center;height:56px;background:#232338;border:1px solid #2e2e48;border-radius:7px;font-size:24px;font-variant-numeric:tabular-nums}.code-cells span.active{border-color:#22c55e;box-shadow:0 0 0 2px rgba(34,197,94,.15)}form[aria-busy=true]{opacity:.65}p[role=alert]{color:#f06b6b}</style><script nonce='{{nonce}}'>{{CodeScript}}</script></body></html>
            """);
    }
}
