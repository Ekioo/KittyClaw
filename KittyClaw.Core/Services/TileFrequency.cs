namespace KittyClaw.Core.Services;

public enum TileFrequencyKind
{
    Never,
    Minutes,
    Hours,
    Days,
    DailyAt,
}

/// <summary>
/// UI-friendly frequency mapping for dashboard tiles. Converts between the persisted
/// sidecar fields (<see cref="TileSidecar.Refresh"/> seconds + optional
/// <see cref="TileSidecar.RefreshAt"/> HH:mm) and a <c>(kind, value, time)</c> tuple used
/// by the tile config popup.
/// </summary>
public static class TileFrequency
{
    public static (int RefreshSeconds, string? RefreshAt) ToSidecar(
        TileFrequencyKind kind, int value, string? time)
    {
        return kind switch
        {
            TileFrequencyKind.Never => (0, null),
            TileFrequencyKind.Minutes => (Math.Max(1, value) * 60, null),
            TileFrequencyKind.Hours => (Math.Max(1, value) * 3600, null),
            TileFrequencyKind.Days => (Math.Max(1, value) * 86400, null),
            TileFrequencyKind.DailyAt => (0, time),
            _ => (0, null),
        };
    }

    public static (TileFrequencyKind Kind, int Value, string? Time) FromSidecar(TileSidecar sidecar)
    {
        if (!string.IsNullOrWhiteSpace(sidecar.RefreshAt))
            return (TileFrequencyKind.DailyAt, 0, sidecar.RefreshAt);

        var seconds = sidecar.Refresh;
        if (seconds <= 0) return (TileFrequencyKind.Never, 0, null);

        if (seconds % 86400 == 0) return (TileFrequencyKind.Days, seconds / 86400, null);
        if (seconds % 3600 == 0) return (TileFrequencyKind.Hours, seconds / 3600, null);
        if (seconds % 60 == 0) return (TileFrequencyKind.Minutes, seconds / 60, null);

        var minutes = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
        return (TileFrequencyKind.Minutes, Math.Max(1, minutes), null);
    }
}
