using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

/// <summary>Applies the assignee implied by a semantic column transition.</summary>
public static class ColumnAssignmentPolicy
{
    public const string OwnerSlug = "owner";

    public static bool Apply(Ticket ticket, BoardColumn? source, BoardColumn destination)
    {
        var previous = ticket.AssignedTo;
        if (destination.Role == ColumnRole.OwnerAction)
            ticket.AssignedTo = OwnerSlug;
        else if (source?.Role == ColumnRole.OwnerAction
                 && string.Equals(ticket.AssignedTo, OwnerSlug, StringComparison.OrdinalIgnoreCase))
            ticket.AssignedTo = null;
        return !string.Equals(previous, ticket.AssignedTo, StringComparison.OrdinalIgnoreCase);
    }
}
