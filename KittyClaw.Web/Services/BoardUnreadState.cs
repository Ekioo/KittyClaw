namespace KittyClaw.Web.Services;

public static class BoardUnreadState
{
    public static bool IsUpdated(
        DateTime activityAt,
        int ticketId,
        IReadOnlyDictionary<int, DateTime> viewedAt,
        DateTime? legacyLastVisitedAt)
    {
        if (viewedAt.TryGetValue(ticketId, out var viewed))
            return activityAt > viewed;

        return legacyLastVisitedAt is not null && activityAt > legacyLastVisitedAt.Value;
    }
}
