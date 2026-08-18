using KittyClaw.Core.Models;

namespace KittyClaw.Web.Services;

public static class BoardUnreadState
{
    public static int CountPipelineUnread(
        IEnumerable<TicketSummary> tickets,
        int pipelineId,
        int? parentId,
        string filterText,
        IReadOnlyDictionary<int, DateTime> viewedAt,
        DateTime? legacyLastVisitedAt,
        Func<TicketPriority, string>? priorityLabel = null)
    {
        var candidates = tickets.Where(ticket =>
            ticket.PipelineId == pipelineId && ticket.ParentId == parentId);

        if (!string.IsNullOrWhiteSpace(filterText))
            candidates = BoardTicketFilter.Apply(candidates, filterText, priorityLabel);

        return candidates.Count(ticket => IsUpdated(
            ticket.LastActivityAt ?? ticket.UpdatedAt,
            ticket.Id,
            viewedAt,
            legacyLastVisitedAt));
    }

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
