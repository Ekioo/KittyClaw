using Todo.Core.Models;

namespace Todo.Core.Automation;

/// <summary>
/// Pure evaluation functions for every <see cref="ConditionSpec"/> type.
/// They operate on already-fetched data (no services, no I/O) so they are
/// trivially unit-testable. AutomationEngine handles data fetching and
/// delegates the actual decision to these helpers.
/// </summary>
public static class ConditionEvaluators
{
    public static bool TicketInColumn(TicketInColumnConditionSpec c, string? firingStatus)
    {
        if (firingStatus is null) return false;
        return c.Columns.Count == 0 || c.Columns.Contains(firingStatus);
    }

    public static bool MinDescriptionLength(MinDescriptionLengthConditionSpec c, string? description)
        => (description ?? string.Empty).Length >= c.Length;

    public static bool FieldLength(FieldLengthConditionSpec c, string? title, string? description)
    {
        var value = c.Field == "title" ? (title ?? "") : (description ?? "");
        return c.Mode == "max" ? value.Length <= c.Length : value.Length >= c.Length;
    }

    public static bool Priority(PriorityConditionSpec c, TicketPriority priority)
        => c.Priorities.Count == 0 || c.Priorities.Contains(priority.ToString());

    public static bool Labels(LabelsConditionSpec c, IReadOnlyCollection<string> labelNames)
        => c.Labels.Count == 0 || labelNames.Any(n => c.Labels.Contains(n));

    public static bool AssignedTo(AssignedToConditionSpec c, string? assignedTo)
        => c.Slugs.Count == 0 ? assignedTo is null : c.Slugs.Contains(assignedTo ?? "");

    public static bool HasParent(HasParentConditionSpec c, int? parentId)
        => c.Value ? parentId is not null : parentId is null;

    public static bool AllSubTicketsInStatus(AllSubTicketsInStatusConditionSpec c, IReadOnlyCollection<SubTicketInfo> subs)
        => subs.Count > 0 && subs.All(s => c.Statuses.Contains(s.Status));

    public static bool TicketAge(TicketAgeConditionSpec c, DateTime createdAt, DateTime updatedAt, DateTime now)
    {
        var field = c.Field == "updatedAt" ? updatedAt : createdAt;
        var hours = (now - field).TotalHours;
        return c.Mode == "newerThan" ? hours < c.Hours : hours >= c.Hours;
    }

    /// <summary>
    /// Compares an observed count to the condition's operator + value.
    /// Supported operators: ==, !=, &lt;, &lt;=, &gt;, &gt;=.
    /// Unknown operator → false.
    /// </summary>
    public static bool CompareCount(string op, int count, int value) => op switch
    {
        "==" => count == value,
        "!=" => count != value,
        "<"  => count < value,
        "<=" => count <= value,
        ">"  => count > value,
        ">=" => count >= value,
        _    => false,
    };

    /// <summary>
    /// Resolves the <c>{assignee}</c> placeholder in an agent slug.
    /// Returns null if the placeholder is present but no assignee is available.
    /// </summary>
    public static string? ResolveAgentPlaceholder(string agent, string? firingAssignee)
    {
        if (!agent.Contains("{assignee}")) return agent;
        if (string.IsNullOrEmpty(firingAssignee)) return null;
        return agent.Replace("{assignee}", firingAssignee);
    }

    /// <summary>
    /// Renders an AddComment content template. Supported placeholders:
    /// <c>{ticketId}</c>, <c>{ticketTitle}</c>, <c>{assignee}</c>.
    /// Missing data is replaced with an empty string.
    /// </summary>
    public static string RenderCommentTemplate(string content, int? ticketId, string? ticketTitle, string? assignee)
        => content
            .Replace("{ticketId}", ticketId?.ToString() ?? "")
            .Replace("{ticketTitle}", ticketTitle ?? "")
            .Replace("{assignee}", assignee ?? "");
}
