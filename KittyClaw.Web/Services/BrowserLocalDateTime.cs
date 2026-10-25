using System.Globalization;

namespace KittyClaw.Web.Services;

/// <summary>
/// Prepares UTC timestamps for browser-side localization. SQLite can materialize
/// UTC values as <see cref="DateTimeKind.Unspecified"/>, so unspecified values are
/// deliberately treated as UTC instead of as the web server's local time.
/// </summary>
public static class BrowserLocalDateTime
{
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static string UtcIso(DateTime value) =>
        AsUtc(value).ToString("O", CultureInfo.InvariantCulture);

    public static string UtcFallback(DateTime value) =>
        $"{AsUtc(value).ToString("g", CultureInfo.CurrentCulture)} UTC";
    public static string Html(DateTime value) =>
        $"<time datetime=\"{UtcIso(value)}\" data-local-date-time>{System.Net.WebUtility.HtmlEncode(UtcFallback(value))}</time>";

    // Encode the localized template and user-controlled arguments before inserting time markup.
    public static Microsoft.AspNetCore.Components.MarkupString Format(string template, params object[] args)
    {
        var encoded = args.Select(arg => arg is DateTime date
            ? Html(date)
            : System.Net.WebUtility.HtmlEncode(Convert.ToString(arg, CultureInfo.CurrentCulture))).ToArray();
        return new(string.Format(CultureInfo.CurrentCulture,
            System.Net.WebUtility.HtmlEncode(template), encoded));
    }
}
