using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Handles ticket-mutation automation actions: move status, set labels, add comment,
/// assign ticket, and create ticket.
/// </summary>
internal sealed class TicketMutationHandler(
    TicketService tickets,
    LabelService labels,
    MemberService members,
    LocalizationService loc,
    ILogger logger)
{
    public async Task ExecuteMoveTicketStatusAsync(
        ProjectRuntime rt, TriggerFiring firing, MoveTicketStatusActionSpec spec, ActionExecutor.ActionState state)
    {
        if (string.Equals(firing.TicketStatus, spec.To, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var ticketBefore = await tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
            state.StatusBeforeMove = ticketBefore?.Status;
            state.AssigneeBeforeMove = ticketBefore?.AssignedTo;
            await tickets.MoveTicketAsync(rt.Slug, firing.TicketId!.Value, spec.To, "automation");
            state.StatusAfterMove = spec.To;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "moveTicketStatus failed for ticket #{Id} in project {Project}",
                firing.TicketId, rt.Slug);
        }
    }

    public async Task ExecuteSetLabelsAsync(ProjectRuntime rt, TriggerFiring firing, SetLabelsActionSpec spec)
    {
        try
        {
            var ticket = await tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
            if (ticket is null) return;
            var currentNames = ticket.Labels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in spec.Add) currentNames.Add(name);
            foreach (var name in spec.Remove) currentNames.Remove(name);
            var allLabels = await labels.ListLabelsAsync(rt.Slug);
            var newIds = allLabels.Where(l => currentNames.Contains(l.Name)).Select(l => l.Id).ToList();
            await tickets.SetTicketLabelsAsync(rt.Slug, firing.TicketId!.Value, newIds);
            var parts = new List<string>();
            if (spec.Add.Count > 0) parts.Add(loc.Get("ActLabelsAdded", string.Join(", ", spec.Add)));
            if (spec.Remove.Count > 0) parts.Add(loc.Get("ActLabelsRemoved", string.Join(", ", spec.Remove)));
            if (parts.Count > 0)
                try
                {
                    await tickets.AddActivityAsync(rt.Slug, firing.TicketId!.Value,
                        loc.Get("ActLabelsChanged", string.Join(" / ", parts)), "automation");
                }
                catch { /* non-blocking */ }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "setLabels failed for ticket #{Id} in project {Project}",
                firing.TicketId, rt.Slug);
        }
    }

    public async Task ExecuteAddCommentAsync(ProjectRuntime rt, TriggerFiring firing, AddCommentActionSpec spec)
    {
        try
        {
            var content = spec.Content
                .Replace("{ticketId}", firing.TicketId?.ToString() ?? "")
                .Replace("{ticketTitle}", firing.TicketTitle ?? "");
            if (content.Contains("{assignee}"))
            {
                var ticket = await tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
                content = content.Replace("{assignee}", ticket?.AssignedTo ?? "");
            }
            await tickets.AddCommentAsync(rt.Slug, firing.TicketId!.Value, content, spec.Author);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "addComment failed for ticket #{Id} in project {Project}",
                firing.TicketId, rt.Slug);
        }
    }

    public async Task ExecuteAssignTicketAsync(ProjectRuntime rt, TriggerFiring firing, AssignTicketActionSpec spec)
    {
        try
        {
            var slug = spec.Slug;
            if (slug is not null && slug.Contains("{previousAssignee}"))
            {
                var ticket = await tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
                slug = slug.Replace("{previousAssignee}", ticket?.AssignedTo ?? "");
            }
            if (string.IsNullOrEmpty(slug))
            {
                await tickets.UpdateTicketAsync(rt.Slug, firing.TicketId!.Value, assignedTo: "", author: "automation");
            }
            else
            {
                var memberList = await members.ListMembersAsync(rt.Slug);
                if (!memberList.Any(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogWarning("assignTicket: member '{Slug}' not found in project {Project}",
                        slug, rt.Slug);
                    return;
                }
                await tickets.UpdateTicketAsync(rt.Slug, firing.TicketId!.Value, assignedTo: slug, author: "automation");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "assignTicket failed for ticket #{Id} in project {Project}",
                firing.TicketId, rt.Slug);
        }
    }

    public async Task ExecuteCreateTicketAsync(ProjectRuntime rt, CreateTicketActionSpec spec)
    {
        try
        {
            var now = DateTime.Now;
            string Resolve(string s) => ActionExecutor.ResolveCreateTicketPlaceholders(s, now);

            var title = Resolve(spec.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                logger.LogWarning("createTicket: resolved title is empty — skipping");
                return;
            }

            if (spec.SkipIfExists)
            {
                var existing = await tickets.ListTicketsAsync(rt.Slug);
                var openStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "Backlog", "Todo", "InProgress", "Blocked", "Review" };
                if (existing.Any(t => openStatuses.Contains(t.Status)
                    && string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogInformation(
                        "createTicket: open ticket with title '{Title}' already exists — skipping", title);
                    return;
                }
            }

            List<int>? labelIds = null;
            if (spec.Labels.Count > 0)
            {
                var allLabels = await labels.ListLabelsAsync(rt.Slug);
                labelIds = allLabels
                    .Where(l => spec.Labels.Any(n => string.Equals(n, l.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(l => l.Id)
                    .ToList();
            }

            var priority = Enum.TryParse<KittyClaw.Core.Models.TicketPriority>(
                spec.Priority, ignoreCase: true, out var p)
                ? p : KittyClaw.Core.Models.TicketPriority.NiceToHave;

            var ticket = await tickets.CreateTicketAsync(
                rt.Slug,
                title,
                description: Resolve(spec.Description),
                createdBy: string.IsNullOrWhiteSpace(spec.CreatedBy) ? "automation" : spec.CreatedBy,
                status: spec.Status,
                labelIds: labelIds,
                priority: priority,
                assignedTo: string.IsNullOrWhiteSpace(spec.AssignedTo) ? null : spec.AssignedTo,
                parentId: spec.ParentId);

            logger.LogInformation("createTicket: created ticket #{Id} '{Title}' in project {Project}",
                ticket.Id, ticket.Title, rt.Slug);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "createTicket failed in project {Project}", rt.Slug);
        }
    }
}
