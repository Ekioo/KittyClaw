namespace KittyClaw.Core.Services;

public sealed record DashboardRefreshTimingState(
    TimeSpan? SinceLastRefresh,
    TimeSpan? UntilNextRefresh,
    bool IsDue);

/// <summary>Computes the relative refresh timing displayed in dashboard tile headers.</summary>
public static class DashboardRefreshTiming
{
    public static DashboardRefreshTimingState Calculate(
        DateTime nowUtc,
        DateTime? lastRefreshUtc,
        TileSidecar? sidecar)
    {
        nowUtc = NormalizeUtc(nowUtc);
        lastRefreshUtc = lastRefreshUtc is null ? null : NormalizeUtc(lastRefreshUtc.Value);
        TimeSpan? sinceLast = lastRefreshUtc is null
            ? null
            : MaxZero(nowUtc - lastRefreshUtc.Value);

        if (sidecar is null)
            return new(sinceLast, null, false);

        DateTime? nextUtc = null;
        if (!string.IsNullOrWhiteSpace(sidecar.RefreshAt)
            && TimeOnly.TryParseExact(sidecar.RefreshAt, "HH:mm", out var refreshAt))
        {
            var nowLocal = nowUtc.ToLocalTime();
            var lastLocal = lastRefreshUtc?.ToLocalTime();
            if (DashboardRefreshScheduling.ShouldFireDailyAt(nowLocal, lastLocal, sidecar.RefreshAt))
            {
                nextUtc = nowUtc;
            }
            else
            {
                var nextLocal = nowLocal.Date.Add(refreshAt.ToTimeSpan());
                if (nextLocal <= nowLocal) nextLocal = nextLocal.AddDays(1);
                nextUtc = nextLocal.ToUniversalTime();
            }
        }
        else if (sidecar.Refresh > 0)
        {
            nextUtc = lastRefreshUtc?.AddSeconds(sidecar.Refresh) ?? nowUtc;
        }

        if (nextUtc is null)
            return new(sinceLast, null, false);

        var untilNext = MaxZero(nextUtc.Value - nowUtc);
        return new(sinceLast, untilNext, untilNext == TimeSpan.Zero);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
