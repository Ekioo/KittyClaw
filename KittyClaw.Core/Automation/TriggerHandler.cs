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
                // Disk changed; wait for explicit reload via API. Just log once.
                _logger.LogInformation("Config change detected on disk for {Slug} — reload requested via UI/API", project.Slug);
                rt.ConfigDirty = false;
            }
            if (rt.Config is null) continue;
            foreach (var automation in rt.Config.Automations)
            {
                if (!automation.Enabled) continue;
                if (!rt.Triggers.TryGetValue(automation.Id, out var trigger)) continue;
                var tctx = BuildTriggerContext(project.Slug, rt.Workspace!, automation);
                IReadOnlyList<TriggerFiring> firings;
                try { firings = await trigger.EvaluateAsync(tctx, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Trigger eval failed for {Id}", automation.Id); continue; }
                foreach (var firing in firings)
                {
                    if (!await _executor.ConditionsMatchAsync(rt, automation, firing)) continue;
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
