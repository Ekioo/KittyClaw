using KittyClaw.Core.Models;

namespace KittyClaw.Web.Services;

public sealed record TicketNumberSearchResult(TicketSummary Ticket, bool IsParent);

public static class TicketNumberSearch
{
    public static bool TryParse(string? query, out int ticketId)
    {
        ticketId = 0;
        if (string.IsNullOrWhiteSpace(query)) return false;

        var trimmed = query.Trim();
        if (int.TryParse(trimmed, out ticketId)) return true;

        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 1 && token[0] == '#' && int.TryParse(token[1..], out ticketId))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<TicketNumberSearchResult> Find(
        IEnumerable<TicketSummary> tickets,
        int ticketId)
    {
        var byId = tickets
            .GroupBy(ticket => ticket.Id)
            .ToDictionary(group => group.Key, group => group.First());
        if (!byId.TryGetValue(ticketId, out var ticket)) return [];

        var results = new List<TicketNumberSearchResult> { new(ticket, false) };
        if (ticket.ParentId is int parentId && parentId != ticket.Id && byId.TryGetValue(parentId, out var parent))
            results.Add(new(parent, true));
        return results;
    }
}
