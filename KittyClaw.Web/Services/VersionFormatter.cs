namespace KittyClaw.Web.Services;

public static class VersionFormatter
{
    public static string Format(string? informational)
    {
        if (string.IsNullOrEmpty(informational)) return "0.0.0";

        var s = informational;
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        var parts = s.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var mj) ? mj : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var mn) ? mn : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var pt) ? pt : 0;
        return $"{major}.{minor}.{patch}";
    }
}
