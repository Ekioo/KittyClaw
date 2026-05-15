using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public sealed class AutomationEngine : BackgroundService
{
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<AutomationEngine> _logger;
    private readonly ProjectRuntimeManager _runtimeManager;
    private readonly TriggerHandler _triggerHandler;

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
        _runs = runs;
        _logger = logger;

        _runtimeManager = new ProjectRuntimeManager(store, logger);
        var runState = new RunStateManager(runs, cost, tickets, logger);
        var executor = new ActionExecutor(tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState, logger);
        _triggerHandler = new TriggerHandler(projects, _runtimeManager, executor, tickets, members, sessions, runs, logger);

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

    private Task TickAsync(CancellationToken ct) => _triggerHandler.ProcessTickAsync(ct);
}
