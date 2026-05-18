namespace KittyClaw.Core.Services;

public static class VersionCompare
{
    public static bool IsNewer(string current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        var trimmed = latest.Trim();
        if (trimmed.StartsWith("v") || trimmed.StartsWith("V")) trimmed = trimmed[1..];
        if (!Version.TryParse(current, out var currentVer)) return false;
        if (!Version.TryParse(trimmed, out var latestVer)) return false;
        return Normalize(latestVer) > Normalize(currentVer);
    }

    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
