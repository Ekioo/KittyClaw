using KittyClaw.Core.Models;

namespace KittyClaw.Web.Services;

public static class BoardTicketFilter
{
    public static IEnumerable<TicketSummary> Apply(
        IEnumerable<TicketSummary> tickets,
        string filterText,
        Func<TicketPriority, string>? priorityLabel = null)
    {
        var tokens = filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token.StartsWith('#') && int.TryParse(token[1..], out var id))
            {
                tickets = tickets.Where(t => t.Id == id);
                continue;
            }
            if (token.StartsWith('@'))
            {
                var slug = token[1..];
                tickets = tickets.Where(t => t.AssignedTo != null &&
                    t.AssignedTo.Contains(slug, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            if (token.StartsWith('>') && DateTime.TryParse(token[1..], out var after))
            {
                tickets = tickets.Where(t => t.UpdatedAt >= after);
                continue;
            }
            if (token.StartsWith('<') && DateTime.TryParse(token[1..], out var before))
            {
                tickets = tickets.Where(t => t.UpdatedAt <= before);
                continue;
            }
            if (token.StartsWith("priority:", StringComparison.OrdinalIgnoreCase))
            {
                var pVal = token[9..];
                tickets = tickets.Where(t =>
                    PriorityClass(t.Priority).Contains(pVal, StringComparison.OrdinalIgnoreCase)
                    || (priorityLabel != null && priorityLabel(t.Priority).Contains(pVal, StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            if (token.StartsWith("label:", StringComparison.OrdinalIgnoreCase))
            {
                var lVal = token[6..];
                tickets = tickets.Where(t => t.Labels.Any(l =>
                    l.Name.Contains(lVal, StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            if (token.StartsWith("by:", StringComparison.OrdinalIgnoreCase))
            {
                var byVal = token[3..];
                tickets = tickets.Where(t =>
                    t.CreatedBy.Contains(byVal, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            tickets = tickets.Where(t =>
                t.Title.Contains(token, StringComparison.OrdinalIgnoreCase)
                || t.Description.Contains(token, StringComparison.OrdinalIgnoreCase));
        }
        return tickets;
    }

    internal static string PriorityClass(TicketPriority p) => p switch
    {
        TicketPriority.Idea => "idea",
        TicketPriority.NiceToHave => "nice",
        TicketPriority.Required => "required",
        TicketPriority.Critical => "critical",
        _ => "nice"
    };
}
