namespace KittyClaw.Core.Automation;

internal static class ColumnActionTemplates
{
    internal static string ResolveCreateTicketPlaceholders(string value, DateTime now)
    {
        var today = now.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstOfMonth = new DateTime(today.Year, today.Month, 1);
        return value
            .Replace("{now}", now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{date}", today.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{monday}", monday.ToString("yyyy-MM-dd"))
            .Replace("{firstOfMonth}", firstOfMonth.ToString("yyyy-MM-dd"));
    }
}
