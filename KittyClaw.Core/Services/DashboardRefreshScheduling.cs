using System.Text.RegularExpressions;

namespace KittyClaw.Core.Services;

/// <summary>
/// Pure-function helpers for deciding whether a dashboard tile with a time-of-day trigger
/// (<see cref="TileSidecar.RefreshAt"/>) should fire now. Kept side-effect free so it can be
/// unit-tested independently of <see cref="DashboardRefreshService"/>.
/// </summary>
public static class DashboardRefreshScheduling
{
    private static readonly Regex _hhmm = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    public static bool ShouldFireDailyAt(DateTime now, DateTime? lastRefresh, string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm) || !_hhmm.IsMatch(hhmm)) return false;

        var parts = hhmm.Split(':');
        var hour = int.Parse(parts[0]);
        var minute = int.Parse(parts[1]);
        var target = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, now.Kind);

        if (now < target) return false;
        if (lastRefresh is null) return true;
        return lastRefresh.Value.Date < now.Date;
    }
}
