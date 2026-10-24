namespace KittyClaw.Web.Services;

/// <summary>Browser protections shared by application, static and access-control responses.</summary>
public sealed class BrowserSecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers.TryAdd("Referrer-Policy", "same-origin");
            // A separate CSP policy intersects with endpoint-specific policies,
            // preserving their sandbox, nonce and resource restrictions.
            headers.Append("Content-Security-Policy", "frame-ancestors 'none'; object-src 'none'; base-uri 'self'");
            return Task.CompletedTask;
        });
        return next(context);
    }
}
