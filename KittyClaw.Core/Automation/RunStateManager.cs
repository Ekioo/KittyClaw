using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Decides whether a RunAgent dispatch should be skipped by checking budget,
/// minimum description length, and all concurrency/mutual-exclusion constraints.
/// Returns true when the dispatch should be skipped.
/// </summary>
internal sealed class RunStateManager
{
    private readonly AgentRunRegistry _runs;
    private readonly CostTracker _cost;
    private readonly TicketService _tickets;
    private readonly ILogger _logger;

    public RunStateManager(AgentRunRegistry runs, CostTracker cost, TicketService tickets, ILogger logger)
    {
        _runs = runs;
        _cost = cost;
        _tickets = tickets;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the automation should NOT be dispatched (budget exceeded,
    /// agent already running, group busy, or mutual-exclusion violated).
    /// </summary>
    public async Task<bool> ShouldSkipAsync(
        ProjectRuntime rt,
        RunAgentActionSpec spec,
        TriggerFiring firing,
        string agentName,
        string group)
    {
        if (rt.Config?.DailyBudgetUsd is decimal cap && group != "ceo"
            && _cost.IsBudgetExceeded(rt.Workspace!, cap))
        {
            _logger.LogInformation("Budget exceeded for {Slug} — skipping {Agent}", rt.Slug, agentName);
            return true;
        }

        if (rt.Config?.MinDescriptionLength is int minLen && firing.TicketId is not null)
        {
            var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            if (t is not null && t.Description.Length < minLen)
            {
                _logger.LogInformation("Ticket #{Id} description too short ({Len}<{Min}) — skipping {Agent}",
                    firing.TicketId, t.Description.Length, minLen, agentName);
                return true;
            }
        }

        if (firing.TicketId is not null
            && _runs.ActiveForTicket(rt.Slug, firing.TicketId.Value).Any(r => r.AgentName == agentName))
            return true;

        if (_runs.HasActiveInGroup(rt.Slug, group)) return true;

        if (spec.MutuallyExclusiveWith.Count > 0)
        {
            var ticketIdStr = firing.TicketId?.ToString() ?? "none";
            var resolved = spec.MutuallyExclusiveWith
                .Select(g => g.Replace("{assignee}", agentName).Replace("{ticketId}", ticketIdStr))
                .ToList();
            if (_runs.HasActiveAny(rt.Slug, resolved))
                return true;
        }

        return false;
    }
}
