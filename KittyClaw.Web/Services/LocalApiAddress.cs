namespace KittyClaw.Web.Services;

/// <summary>Server-side UI calls stay on the trusted listener even when the browser is remote.</summary>
internal static class LocalApiAddress
{
    internal static Uri Resolve(IEnumerable<string> addresses, int tunnelPort)
    {
        var candidates = addresses.Select(value => Uri.TryCreate(value.Replace("*", "localhost").Replace("+", "localhost"),
            UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && uri.Port > 0 && uri.Port != tunnelPort)
            .OrderBy(uri => uri!.Scheme == "http" ? 0 : 1);
        foreach (var uri in candidates)
            if (uri!.Scheme is "http" or "https") return new UriBuilder(uri.Scheme, "localhost", uri.Port).Uri;
        throw new InvalidOperationException("The trusted local API listener is not available.");
    }
}
