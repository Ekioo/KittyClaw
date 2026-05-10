using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public sealed class AutomationEngine : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<AutomationEngine> _logger;

    private readonly ProjectRuntimeManager _runtimeManager;
    private readonly ActionExecutor _executor;

    public AutomationEngine(
        ProjectService projects,
        TicketService tickets,
        MemberService members,
        LabelService labels,
        AutomationStore store,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        ClaudeRunner runner,
        CostTracker cost,
        LocalizationService loc,
        ILogger<AutomationEngine> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _members = members;
        _sessions = sessions;
        _runs = runs;
        _logger = logger;

        _runtimeManager = new ProjectRuntimeManager(store, logger);
        _executor = new ActionExecutor(tickets, members, labels, sessions, runs, runner, cost, loc, projects, logger);

        store.OnConfigChangedOnDisk += slug =>
        {
            if (_runtimeManager.TryGetRuntime(slug, out var rt) && rt is not null)
                rt.ConfigDirty = true;
        };

        tickets.TicketStatusChanged += (slug, ticketId, from, to) =>
            _ = NotifySignalAsync(slug, new StatusChangeSignal(ticketId, from, to));

        tickets.TicketCommentAdded += (slug, ticketId, author, content) =>
            _ = NotifySignalAsync(slug, new CommentAddedSignal(ticketId, author, content));
    }

    public Task ReloadProjectAsync(string slug) => _runtimeManager.ReloadProjectAsync(slug);

    /// <summary>
    /// Push an external signal to all enabled automations of <paramref name="projectSlug"/>.
    /// Each trigger that implements <see cref="ITrigger.TryHandleExternalSignal"/> can produce
    /// firings that are enqueued and dispatched at the beginning of the very next tick (&lt;1 s).
    /// </summary>
    public Task NotifySignalAsync(string projectSlug, object signal) =>
        _runtimeManager.NotifySignalAsync(projectSlug, signal);

    /// <summary>
    /// Returns the next predicted UTC fire time for each automation in the project,
    /// keyed by automation ID. Returns null for event-driven triggers with no predictable schedule.
    /// Triggers a background load if the project runtime is not yet initialized.
    /// </summary>
    public Dictionary<string, DateTime?> GetNextRunTimes(string projectSlug) =>
        _runtimeManager.GetNextRunTimes(projectSlug);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutomationEngine started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutomationEngine tick failed");
            }
            _runs.PurgeOld(TimeSpan.FromHours(24));
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
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
                rt.ConfigDirty = false; // don't spam; next real change will flag again
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
