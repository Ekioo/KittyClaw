using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Evaluates triggers each engine tick: drains urgent signal firings first,
/// then polls each project's automations for scheduled/condition-based firings.
/// </summary>
internal sealed class TriggerHandler
{
    private readonly ProjectService _projects;
    private readonly ProjectRuntimeManager _runtimeManager;
    private readonly ActionExecutor _executor;
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly ILogger _logger;

    public TriggerHandler(
        ProjectService projects,
        ProjectRuntimeManager runtimeManager,
        ActionExecutor executor,
        TicketService tickets,
        MemberService members,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        ILogger logger)
    {
        _projects = projects;
        _runtimeManager = runtimeManager;
        _executor = executor;
        _tickets = tickets;
        _members = members;
        _sessions = sessions;
        _runs = runs;
        _logger = logger;
    }

    public async Task ProcessTickAsync(CancellationToken ct)
    {
        // Drain urgent firings first (produced by NotifySignalAsync) before the regular poll.
        while (_runtimeManager.UrgentReader.TryRead(out var entry))
        {
            if (ct.IsCancellationRequested) return;
            var urgentProject = await _projects.GetProjectAsync(entry.Slug);
            if (urgentProject?.IsPaused == true) continue;
            await _runtimeManager.EnsureLoadedAsync(entry.Slug);
            if (!_runtimeManager.TryGetRuntime(entry.Slug, out var urt) || urt?.Config is null) continue;
            if (!await _executor.ConditionsMatchAsync(urt, entry.Automation, entry.Firing)) continue;
            var utctx = BuildTriggerContext(entry.Slug, urt.Workspace!, entry.Automation);
            urt.LastFiredAt = DateTime.UtcNow;
            urt.LastFiredAutomationId = entry.Automation.Id;
            await _executor.ExecuteAutomationAsync(urt, entry.Automation, entry.Firing, ct, entry.Trigger, utctx);
        }

        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) return;
            if (project.IsPaused) continue;
            await _runtimeManager.EnsureLoadedAsync(project.Slug);
            var rt = _runtimeManager.GetRuntime(project.Slug);
            if (rt.ConfigDirty)
            {
                // Disk changed outside the PUT /automations API (e.g. an agent editing automations.json
                // directly) — reload now so newly added/edited automations aren't silently unregistered
                // until someone happens to hit the UI's "Reload" button. See ticket lain#181/kittyclaw-front#116:
                // automations added this way sat dormant in rt.Triggers for weeks with no error or indication.
                _logger.LogInformation("Config change detected on disk for {Slug} — reloading", project.Slug);
                await _runtimeManager.ReloadProjectAsync(project.Slug);
            }
            if (rt.Config is null) continue;
            // First-match-wins per ticket per tick (ticket #112): column-poll automations are
            // evaluated in file order, and the first one that matches AND dispatches on a ticket
            // consumes it for this tick. Without this, two ticketInColumn automations watching
            // the same column race on the same ticket — one's runAgent effects land asynchronously
            // and the other's state changes get overwritten (kalceo #1144–#1148: tickets moved to
            // InProgress with an empty assignee, invisible to the router, stuck for 2 days).
            var consumedTickets = new HashSet<int>();
            foreach (var automation in rt.Config.Automations)
            {
                if (!automation.Enabled) continue;
                if (!rt.Triggers.TryGetValue(automation.Id, out var trigger)) continue;
                var isColumnPoll = trigger is TicketInColumnTrigger;
                var tctx = BuildTriggerContext(project.Slug, rt.Workspace!, automation);
                IReadOnlyList<TriggerFiring> firings;
                try { firings = await trigger.EvaluateAsync(tctx, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Trigger eval failed for {Id}", automation.Id); continue; }
                foreach (var firing in firings)
                {
                    if (isColumnPoll && firing.TicketId is int consumedId && consumedTickets.Contains(consumedId))
                    {
                        _logger.LogInformation(
                            "Automation {Id} skipped ticket #{Ticket}: consumed by an earlier column-poll automation this tick (first-match-wins)",
                            automation.Id, consumedId);
                        continue;
                    }
                    if (!await _executor.ConditionsMatchAsync(rt, automation, firing)) continue;
                    if (isColumnPoll && firing.TicketId is int dispatchedId) consumedTickets.Add(dispatchedId);
                    rt.LastFiredAt = DateTime.UtcNow;
                    rt.LastFiredAutomationId = automation.Id;
                    // Awaited: the prep phase runs to completion before the next firing, reserving
                    // concurrency slots. The actual subprocess is fire-and-forget inside ExecuteRunAgentActionAsync.
                    await _executor.ExecuteAutomationAsync(rt, automation, firing, ct, trigger, tctx);
                }
            }
        }
    }

    private TriggerContext BuildTriggerContext(string slug, string workspace, Automation automation) =>
        new()
        {
            ProjectSlug = slug,
            WorkspacePath = workspace,
            Automation = automation,
            Tickets = _tickets,
            Members = _members,
            Sessions = _sessions,
            Runs = _runs,
            Now = DateTime.UtcNow,
        };
}
