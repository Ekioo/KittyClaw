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
}
