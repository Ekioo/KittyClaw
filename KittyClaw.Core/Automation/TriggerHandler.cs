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
    private readonly AutomationQueueStore _queue;

    public TriggerHandler(ProjectService projects, ProjectRuntimeManager runtimeManager,
        ActionExecutor executor, TicketService tickets, MemberService members,
        SessionRegistry sessions, AgentRunRegistry runs, ILogger logger)
        : this(projects, runtimeManager, executor, tickets, members, sessions, runs,
            new AutomationQueueStore(projects), logger) { }

    public TriggerHandler(
        ProjectService projects,
        ProjectRuntimeManager runtimeManager,
        ActionExecutor executor,
        TicketService tickets,
        MemberService members,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        AutomationQueueStore queue,
        ILogger logger)
    {
        _projects = projects;
        _runtimeManager = runtimeManager;
        _executor = executor;
        _tickets = tickets;
        _members = members;
        _sessions = sessions;
        _runs = runs;
        _queue = queue;
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
            var utctx = BuildTriggerContext(entry.Slug, urt.Workspace!, entry.Automation);
            if (!entry.Firing.ShouldDispatch)
            {
                if (entry.Trigger is StatusChangeTrigger observationTrigger)
                    observationTrigger.TryConsumeFiring(utctx, entry.Firing);
                continue;
            }
            if (!await _executor.ConditionsMatchAsync(urt, entry.Automation, entry.Firing)) continue;
            if (entry.Trigger is StatusChangeTrigger statusTrigger && !statusTrigger.TryConsumeFiring(utctx, entry.Firing))
            {
                _logger.LogInformation(
                    "Automation {Id} suppressed duplicate status transition for ticket #{Ticket} in {Status}",
                    entry.Automation.Id, entry.Firing.TicketId, entry.Firing.TicketStatus);
                continue;
            }
            // Consume the underlying event (persisted) before dispatch so the poll path does
            // not re-fire it. On failure, dispatch anyway: a ~PollSeconds-late duplicate is
            // better than a lost firing (ticket #136).
            try
            {
                if (entry.Trigger is not StatusChangeTrigger)
                    await entry.Trigger.ConsumeSignalFiringAsync(utctx, entry.Firing);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "ConsumeSignalFiring failed for {Id} — the poll may re-fire this event", entry.Automation.Id); }
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
            // Occurrence tracking must see every column transition, including columns that
            // are not watched by any ticketInColumn automation. Otherwise a later re-entry
            // into a watched column is incorrectly deduplicated against the previous visit.
            foreach (var ticket in await _tickets.ListTicketsAsync(project.Slug))
                await _queue.ObserveColumnAsync(project.Slug, ticket.Id, ticket.Status, ct);
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
                    if (!await _executor.ConditionsMatchAsync(rt, automation, firing)) continue;
                    if (isColumnPoll && firing.TicketId is int ticketId)
                    {
                        var ticket = await _tickets.GetTicketAsync(project.Slug, ticketId);
                        if (ticket is not null)
                            await _queue.EnqueueAsync(project.Slug, ticket, [automation], ct);
                        continue;
                    }
                    if (trigger is StatusChangeTrigger statusTrigger && !statusTrigger.TryConsumeFiring(tctx, firing))
                    {
                        _logger.LogInformation(
                            "Automation {Id} suppressed duplicate status transition for ticket #{Ticket} in {Status}",
                            automation.Id, firing.TicketId, firing.TicketStatus);
                        continue;
                    }
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
